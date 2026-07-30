namespace ArisenEngine.Terrain.Assets;

public enum TerrainWeightSourceFormat
{
    Rgba8Hex = 1
}

public sealed class TerrainWeightField
{
    private readonly byte[] m_Weights;

    internal TerrainWeightField(int width, int height, byte[] weights)
    {
        Width = width;
        Height = height;
        m_Weights = weights;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Weights => m_Weights;

    public ReadOnlySpan<byte> GetSample(int x, int z)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Terrain weight sample ({x}, {z}) is outside {Width}x{Height}.");
        }

        int offset = checked(
            ((z * Width) + x) * TerrainCookedFormat.WeightChannelCount);
        return m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount);
    }
}

public static class TerrainWeightSourceDecoder
{
    public const int MaximumEncodedBytes = 192 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "ARIWEIGHTS"u8;

    public static TerrainWeightField DecodeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(
                $"[TerrainWeightSourceDecoder] Weight source is missing: {path}",
                path);
        }
        if (info.Length <= 0 || info.Length > MaximumEncodedBytes)
        {
            throw Invalid(path, $"encoded byte count '{info.Length}' is outside the supported range");
        }

        return Decode(File.ReadAllBytes(path), path);
    }

    public static TerrainWeightField Decode(
        ReadOnlySpan<byte> encoded,
        string diagnosticPath = "<memory>")
    {
        var reader = new TokenReader(encoded, diagnosticPath);
        if (!reader.Read().SequenceEqual(Magic))
        {
            throw Invalid(diagnosticPath, "expected ARIWEIGHTS magic");
        }
        if (ReadPositiveInt(reader.Read(), diagnosticPath, "version") != 1)
        {
            throw Invalid(diagnosticPath, "supports only schema version 1");
        }

        int width = ReadPositiveInt(reader.Read(), diagnosticPath, "width");
        int height = ReadPositiveInt(reader.Read(), diagnosticPath, "height");
        if (width > TerrainHeightSourceDecoder.MaxDimension ||
            height > TerrainHeightSourceDecoder.MaxDimension)
        {
            throw Invalid(
                diagnosticPath,
                $"dimensions {width}x{height} exceed {TerrainHeightSourceDecoder.MaxDimension}");
        }

        int sampleCount = checked(width * height);
        var weights = new byte[
            checked(sampleCount * TerrainCookedFormat.WeightChannelCount)];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            ReadOnlySpan<byte> token = reader.Read();
            if (token.Length != 8)
            {
                throw Invalid(
                    diagnosticPath,
                    $"sample '{sample}' must contain exactly eight hexadecimal digits");
            }

            int offset = sample * TerrainCookedFormat.WeightChannelCount;
            for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
            {
                weights[offset + channel] = checked((byte)(
                    (ReadHex(token[channel * 2], diagnosticPath, sample) << 4) |
                    ReadHex(token[(channel * 2) + 1], diagnosticPath, sample)));
            }
        }

        if (reader.HasMore)
        {
            throw Invalid(diagnosticPath, "contains trailing sample data");
        }

        return new TerrainWeightField(width, height, weights);
    }

    private static int ReadPositiveInt(
        ReadOnlySpan<byte> token,
        string path,
        string field)
    {
        if (token.IsEmpty)
        {
            throw Invalid(path, $"is missing {field}");
        }

        int value = 0;
        for (int index = 0; index < token.Length; index++)
        {
            byte digit = token[index];
            if (digit is < (byte)'0' or > (byte)'9')
            {
                throw Invalid(path, $"{field} must be a positive decimal integer");
            }

            value = checked((value * 10) + digit - '0');
        }

        if (value <= 0)
        {
            throw Invalid(path, $"{field} must be greater than zero");
        }

        return value;
    }

    private static int ReadHex(byte value, string path, int sample)
    {
        if (value is >= (byte)'0' and <= (byte)'9') return value - '0';
        if (value is >= (byte)'a' and <= (byte)'f') return value - 'a' + 10;
        if (value is >= (byte)'A' and <= (byte)'F') return value - 'A' + 10;
        throw Invalid(path, $"sample '{sample}' contains a non-hexadecimal digit");
    }

    private static InvalidDataException Invalid(string path, string diagnostic) =>
        new($"[TerrainWeightSourceDecoder] Weight source '{path}' {diagnostic}.");

    private ref struct TokenReader
    {
        private readonly ReadOnlySpan<byte> m_Encoded;
        private readonly string m_Path;
        private int m_Offset;

        public TokenReader(ReadOnlySpan<byte> encoded, string path)
        {
            if (encoded.IsEmpty || encoded.Length > MaximumEncodedBytes)
            {
                throw Invalid(path, $"encoded byte count '{encoded.Length}' is outside the supported range");
            }

            m_Encoded = encoded;
            m_Path = path;
            m_Offset = 0;
        }

        public bool HasMore
        {
            get
            {
                SkipWhitespace();
                return m_Offset < m_Encoded.Length;
            }
        }

        public ReadOnlySpan<byte> Read()
        {
            SkipWhitespace();
            int start = m_Offset;
            while (m_Offset < m_Encoded.Length && !IsWhitespace(m_Encoded[m_Offset]))
            {
                if (m_Encoded[m_Offset] > 0x7f)
                {
                    throw Invalid(m_Path, $"contains non-ASCII byte at offset '{m_Offset}'");
                }
                m_Offset++;
            }

            if (start == m_Offset)
            {
                throw Invalid(m_Path, "ended before all required fields were read");
            }

            return m_Encoded[start..m_Offset];
        }

        private void SkipWhitespace()
        {
            while (m_Offset < m_Encoded.Length && IsWhitespace(m_Encoded[m_Offset]))
            {
                m_Offset++;
            }
        }

        private static bool IsWhitespace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }
}

public static class TerrainWeightSourceEncoder
{
    private static ReadOnlySpan<byte> HexDigits => "0123456789abcdef"u8;

    public static byte[] Encode(
        int width,
        int height,
        ReadOnlySpan<byte> weights)
    {
        if (width <= 0 || height <= 0 ||
            width > TerrainHeightSourceDecoder.MaxDimension ||
            height > TerrainHeightSourceDecoder.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Terrain weight dimensions are outside the supported range.");
        }

        int sampleCount = checked(width * height);
        int expectedWeightCount = checked(
            sampleCount * TerrainCookedFormat.WeightChannelCount);
        if (weights.Length != expectedWeightCount)
        {
            throw new ArgumentException(
                "Terrain weight sample count does not match its dimensions.",
                nameof(weights));
        }

        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            $"ARIWEIGHTS\n1\n{width} {height}\n");
        int encodedLength = checked(header.Length + (sampleCount * 9));
        if (encodedLength > TerrainWeightSourceDecoder.MaximumEncodedBytes)
        {
            throw new InvalidOperationException(
                $"Encoded terrain weights require {encodedLength} bytes, exceeding " +
                $"{TerrainWeightSourceDecoder.MaximumEncodedBytes}.");
        }

        var encoded = new byte[encodedLength];
        header.CopyTo(encoded, 0);
        int destination = header.Length;
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int sample = checked((z * width) + x);
                int source = sample * TerrainCookedFormat.WeightChannelCount;
                for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
                {
                    byte value = weights[source + channel];
                    encoded[destination++] = HexDigits[value >> 4];
                    encoded[destination++] = HexDigits[value & 0x0f];
                }

                encoded[destination++] = x + 1 == width ? (byte)'\n' : (byte)' ';
            }
        }

        if (destination != encoded.Length)
        {
            throw new InvalidOperationException(
                "Terrain weight encoding produced an unexpected byte count.");
        }

        return encoded;
    }
}
