using System.Buffers.Binary;
using System.Globalization;

namespace ArisenEngine.Terrain.Assets;

public sealed class TerrainHeightField
{
    private readonly ushort[] m_Samples;

    internal TerrainHeightField(int width, int height, ushort[] samples)
    {
        Width = width;
        Height = height;
        m_Samples = samples;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<ushort> Samples => m_Samples;

    public ushort GetSample(int x, int z)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Terrain sample ({x}, {z}) is outside {Width}x{Height}.");
        }

        return m_Samples[checked((z * Width) + x)];
    }
}

public static class TerrainHeightSourceDecoder
{
    public const int MaxDimension = 65_537;
    public const int MaxSampleCount = 67_108_864;
    public const int MaxHeaderSize = 4_096;

    private const int MaxHeaderTokenLength = 64;
    private const long MaxEncodedSourceSize =
        MaxHeaderSize + ((long)MaxSampleCount * sizeof(ushort));

    public static TerrainHeightField DecodeFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Terrain height decoding requires a source path.", nameof(sourcePath));
        }

        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaxEncodedSourceSize)
        {
            throw Invalid(
                sourcePath,
                $"size '{stream.Length}' exceeds the supported maximum {MaxEncodedSourceSize}");
        }

        var source = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(source);
        if (stream.ReadByte() >= 0)
        {
            throw Invalid(sourcePath, "changed size while it was being decoded");
        }

        return Decode(source, sourcePath);
    }

    public static TerrainHeightField Decode(
        ReadOnlySpan<byte> source,
        string diagnosticPath = "<memory>")
    {
        if (source.Length > MaxEncodedSourceSize)
        {
            throw Invalid(
                diagnosticPath,
                $"size '{source.Length}' exceeds the supported maximum {MaxEncodedSourceSize}");
        }

        var parser = new PgmHeaderParser(source, diagnosticPath);
        string magic = parser.NextToken("magic");
        if (!string.Equals(magic, "P5", StringComparison.Ordinal))
        {
            throw Invalid(
                diagnosticPath,
                $"uses PGM magic '{magic}', expected binary grayscale P5");
        }

        int width = parser.NextPositiveInt32("width");
        int height = parser.NextPositiveInt32("height");
        int maxValue = parser.NextPositiveInt32("maximum value");
        if (maxValue != ushort.MaxValue)
        {
            throw Invalid(
                diagnosticPath,
                $"declares maximum value '{maxValue}', expected exactly 65535 for lossless 16-bit scalar heights");
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            throw Invalid(
                diagnosticPath,
                $"dimensions {width}x{height} exceed the supported maximum dimension {MaxDimension}");
        }

        int sampleCount;
        try
        {
            sampleCount = checked(width * height);
        }
        catch (OverflowException)
        {
            throw Invalid(diagnosticPath, $"dimensions {width}x{height} overflow the supported sample range");
        }

        if (sampleCount > MaxSampleCount)
        {
            throw Invalid(
                diagnosticPath,
                $"sample count '{sampleCount}' exceeds the supported maximum {MaxSampleCount}");
        }

        int payloadOffset = parser.ConsumeRasterSeparator();
        int expectedPayloadSize = checked(sampleCount * sizeof(ushort));
        int actualPayloadSize = source.Length - payloadOffset;
        if (actualPayloadSize != expectedPayloadSize)
        {
            string problem = actualPayloadSize < expectedPayloadSize ? "truncated" : "contains trailing data";
            throw Invalid(
                diagnosticPath,
                $"raster is {problem}: expected {expectedPayloadSize} bytes, found {actualPayloadSize}");
        }

        ReadOnlySpan<byte> raster = source[payloadOffset..];
        var samples = new ushort[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            samples[index] = BinaryPrimitives.ReadUInt16BigEndian(
                raster.Slice(index * sizeof(ushort), sizeof(ushort)));
        }

        return new TerrainHeightField(width, height, samples);
    }

    private static InvalidDataException Invalid(string path, string diagnostic)
    {
        return new InvalidDataException(
            $"[TerrainHeightSourceDecoder] Height source '{path}' {diagnostic}.");
    }

    private ref struct PgmHeaderParser
    {
        private readonly ReadOnlySpan<byte> m_Source;
        private readonly string m_Path;
        private int m_Index;

        public PgmHeaderParser(ReadOnlySpan<byte> source, string path)
        {
            m_Source = source;
            m_Path = path;
            m_Index = 0;
        }

        public int NextPositiveInt32(string field)
        {
            string token = NextToken(field);
            if (!int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value) ||
                value <= 0)
            {
                throw Invalid(m_Path, $"has invalid {field} token '{token}'");
            }

            return value;
        }

        public string NextToken(string field)
        {
            SkipWhitespaceAndComments();
            if (m_Index >= m_Source.Length)
            {
                throw Invalid(m_Path, $"ended before the {field} token");
            }

            int start = m_Index;
            while (m_Index < m_Source.Length &&
                   !IsAsciiWhitespace(m_Source[m_Index]) &&
                   m_Source[m_Index] != (byte)'#')
            {
                byte value = m_Source[m_Index];
                if (value > 0x7f)
                {
                    throw Invalid(m_Path, $"contains a non-ASCII byte in the {field} token");
                }

                m_Index++;
                EnsureHeaderWithinLimit();
                if (m_Index - start > MaxHeaderTokenLength)
                {
                    throw Invalid(
                        m_Path,
                        $"has a {field} token longer than {MaxHeaderTokenLength} ASCII bytes");
                }
            }

            if (m_Index == start)
            {
                throw Invalid(m_Path, $"has an empty {field} token");
            }

            return System.Text.Encoding.ASCII.GetString(m_Source[start..m_Index]);
        }

        public int ConsumeRasterSeparator()
        {
            if (m_Index >= m_Source.Length || !IsAsciiWhitespace(m_Source[m_Index]))
            {
                throw Invalid(
                    m_Path,
                    "must have one ASCII whitespace separator between the maximum value and raster");
            }

            byte separator = m_Source[m_Index++];
            if (separator == (byte)'\r' &&
                m_Index < m_Source.Length &&
                m_Source[m_Index] == (byte)'\n')
            {
                m_Index++;
            }

            EnsureHeaderWithinLimit();

            return m_Index;
        }

        private void SkipWhitespaceAndComments()
        {
            while (m_Index < m_Source.Length)
            {
                if (IsAsciiWhitespace(m_Source[m_Index]))
                {
                    m_Index++;
                    EnsureHeaderWithinLimit();
                    continue;
                }

                if (m_Source[m_Index] != (byte)'#')
                {
                    return;
                }

                while (m_Index < m_Source.Length &&
                       m_Source[m_Index] != (byte)'\r' &&
                       m_Source[m_Index] != (byte)'\n')
                {
                    if (m_Source[m_Index] > 0x7f)
                    {
                        throw Invalid(m_Path, "contains a non-ASCII byte in its header");
                    }

                    m_Index++;
                    EnsureHeaderWithinLimit();
                }
            }
        }

        private void EnsureHeaderWithinLimit()
        {
            if (m_Index > MaxHeaderSize)
            {
                throw Invalid(
                    m_Path,
                    $"header exceeds the supported maximum {MaxHeaderSize} bytes");
            }
        }

        private static bool IsAsciiWhitespace(byte value)
        {
            return value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0b or 0x0c;
        }
    }
}

public static class TerrainHeightSourceEncoder
{
    public static byte[] Encode(
        int width,
        int height,
        ReadOnlySpan<ushort> samples)
    {
        if (width <= 0 || height <= 0 ||
            width > TerrainHeightSourceDecoder.MaxDimension ||
            height > TerrainHeightSourceDecoder.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Terrain height dimensions are outside the supported range.");
        }

        int sampleCount = checked(width * height);
        if (sampleCount > TerrainHeightSourceDecoder.MaxSampleCount ||
            samples.Length != sampleCount)
        {
            throw new ArgumentException(
                "Terrain height sample count does not match its dimensions.",
                nameof(samples));
        }

        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"P5\n{width} {height}\n65535\n"));
        var encoded = new byte[checked(header.Length + (sampleCount * sizeof(ushort)))];
        header.CopyTo(encoded, 0);
        for (int index = 0; index < sampleCount; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                encoded.AsSpan(
                    header.Length + (index * sizeof(ushort)),
                    sizeof(ushort)),
                samples[index]);
        }

        return encoded;
    }
}
