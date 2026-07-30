using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArisenEngine.Terrain.Assets;

[Flags]
internal enum TerrainCookedSectionFlags : uint
{
    None = 0,
    Required = 1 << 0
}

internal readonly record struct TerrainCookedSectionPayload(
    uint Type,
    TerrainCookedSectionFlags Flags,
    uint Count,
    uint Stride,
    byte[] Bytes);

internal readonly record struct TerrainCookedSectionDescriptor(
    uint Type,
    TerrainCookedSectionFlags Flags,
    ulong Offset,
    ulong Size,
    uint Count,
    uint Stride);

internal static class TerrainCookedContainer
{
    internal const uint EndianMarker = 0x01020304;
    internal const int SectionDirectoryEntrySize = 32;
    internal const int HashSize = 32;
    internal const int MaxSectionCount = 32;

    private const uint SupportedSectionFlags = (uint)TerrainCookedSectionFlags.Required;
    private const uint MaxSectionElementCount = 67_108_864;
    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    internal static byte[] Build(
        int headerSize,
        int maximumBytes,
        IReadOnlyList<TerrainCookedSectionPayload> sections,
        out TerrainCookedSectionDescriptor[] descriptors)
    {
        if (sections.Count is <= 0 or > MaxSectionCount)
        {
            throw new InvalidOperationException(
                $"[TerrainCookedContainer] Section count '{sections.Count}' is outside the supported range.");
        }

        int directorySize = checked(sections.Count * SectionDirectoryEntrySize);
        int nextOffset = Align8(checked(headerSize + directorySize));
        descriptors = new TerrainCookedSectionDescriptor[sections.Count];
        uint previousType = 0;
        for (int index = 0; index < sections.Count; index++)
        {
            TerrainCookedSectionPayload section = sections[index];
            if (section.Type == 0 || (index > 0 && section.Type <= previousType))
            {
                throw new InvalidOperationException(
                    "[TerrainCookedContainer] Sections must have unique, strictly increasing nonzero types.");
            }

            if (((uint)section.Flags & ~SupportedSectionFlags) != 0)
            {
                throw new InvalidOperationException(
                    $"[TerrainCookedContainer] Section '{section.Type}' has unsupported flags.");
            }

            if (section.Count > MaxSectionElementCount ||
                (section.Stride != 0 && section.Bytes.Length != checked((long)section.Count * section.Stride)))
            {
                throw new InvalidOperationException(
                    $"[TerrainCookedContainer] Section '{section.Type}' has inconsistent count, stride, or size.");
            }

            descriptors[index] = new TerrainCookedSectionDescriptor(
                section.Type,
                section.Flags,
                checked((ulong)nextOffset),
                checked((ulong)section.Bytes.Length),
                section.Count,
                section.Stride);
            nextOffset = Align8(checked(nextOffset + section.Bytes.Length));
            previousType = section.Type;
        }

        if (nextOffset > maximumBytes)
        {
            throw new InvalidOperationException(
                $"[TerrainCookedContainer] Cooked size '{nextOffset}' exceeds the {maximumBytes}-byte limit.");
        }

        byte[] output = new byte[nextOffset];
        Span<byte> outputBytes = output;
        for (int index = 0; index < descriptors.Length; index++)
        {
            TerrainCookedSectionDescriptor descriptor = descriptors[index];
            WriteDescriptor(
                outputBytes.Slice(
                    headerSize + (index * SectionDirectoryEntrySize),
                    SectionDirectoryEntrySize),
                descriptor);
            sections[index].Bytes.CopyTo(
                outputBytes.Slice(checked((int)descriptor.Offset), sections[index].Bytes.Length));
        }

        return output;
    }

    internal static Dictionary<uint, TerrainCookedSectionDescriptor> ReadDirectory(
        ReadOnlySpan<byte> bytes,
        int headerSize,
        int hashOffset,
        int sectionCount,
        int maximumBytes,
        IReadOnlySet<uint> knownSectionTypes,
        string context)
    {
        if (bytes.Length < headerSize || bytes.Length > maximumBytes)
        {
            throw Invalid(
                context,
                $"byte length '{bytes.Length}' is outside the supported bounds {headerSize}..{maximumBytes}");
        }

        if (sectionCount is <= 0 or > MaxSectionCount)
        {
            throw Invalid(context, $"section count '{sectionCount}' is invalid");
        }

        int directoryEnd = checked(headerSize + (sectionCount * SectionDirectoryEntrySize));
        if (directoryEnd > bytes.Length)
        {
            throw Invalid(context, "section directory is truncated");
        }

        byte[] computedHash = SHA256.HashData(bytes[headerSize..]);
        if (!CryptographicOperations.FixedTimeEquals(
                bytes.Slice(hashOffset, HashSize),
                computedHash))
        {
            throw Invalid(context, "content hash does not match the section directory and payload");
        }

        var sections = new Dictionary<uint, TerrainCookedSectionDescriptor>(sectionCount);
        var ranges = new List<(ulong Start, ulong End, uint Type)>(sectionCount);
        uint previousType = 0;
        for (int index = 0; index < sectionCount; index++)
        {
            ReadOnlySpan<byte> descriptorBytes = bytes.Slice(
                headerSize + (index * SectionDirectoryEntrySize),
                SectionDirectoryEntrySize);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes);
            uint rawFlags = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[4..]);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(descriptorBytes[8..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(descriptorBytes[16..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[24..]);
            uint stride = BinaryPrimitives.ReadUInt32LittleEndian(descriptorBytes[28..]);

            if (type == 0 || (index > 0 && type <= previousType))
            {
                throw Invalid(context, "section types are not unique and strictly increasing");
            }

            if ((rawFlags & ~SupportedSectionFlags) != 0)
            {
                throw Invalid(context, $"section type '{type}' uses unsupported flags '0x{rawFlags:X8}'");
            }

            if ((offset & 7) != 0 || offset < checked((ulong)directoryEnd))
            {
                throw Invalid(context, $"section type '{type}' has invalid or unaligned offset '{offset}'");
            }

            ulong end;
            try
            {
                end = checked(offset + size);
            }
            catch (OverflowException ex)
            {
                throw Invalid(
                    context,
                    $"section type '{type}' range overflows the 64-bit container address space",
                    ex);
            }
            if (end > checked((ulong)bytes.Length))
            {
                throw Invalid(context, $"section type '{type}' extends beyond the file");
            }

            if (count > MaxSectionElementCount ||
                (stride != 0 && size != checked((ulong)count * stride)))
            {
                throw Invalid(context, $"section type '{type}' has inconsistent count, stride, or size");
            }

            bool known = knownSectionTypes.Contains(type);
            if (!known && (rawFlags & (uint)TerrainCookedSectionFlags.Required) != 0)
            {
                throw Invalid(context, $"unknown required section type '{type}' cannot be skipped");
            }

            var descriptor = new TerrainCookedSectionDescriptor(
                type,
                (TerrainCookedSectionFlags)rawFlags,
                offset,
                size,
                count,
                stride);
            if (known)
            {
                sections.Add(type, descriptor);
            }

            if (size > 0)
            {
                ranges.Add((offset, end, type));
            }

            previousType = type;
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
            {
                throw Invalid(
                    context,
                    $"sections '{ranges[index - 1].Type}' and '{ranges[index].Type}' overlap");
            }
        }

        ulong paddingCursor = checked((ulong)directoryEnd);
        foreach ((ulong start, ulong end, _) in ranges)
        {
            EnsureZero(
                bytes.Slice(
                    checked((int)paddingCursor),
                    checked((int)(start - paddingCursor))),
                context,
                "section alignment padding");
            paddingCursor = end;
        }

        EnsureZero(
            bytes.Slice(
                checked((int)paddingCursor),
                checked(bytes.Length - (int)paddingCursor)),
            context,
            "trailing alignment padding");
        return sections;
    }

    internal static TerrainCookedSectionDescriptor RequireSection(
        IReadOnlyDictionary<uint, TerrainCookedSectionDescriptor> sections,
        uint type,
        uint expectedStride,
        uint? expectedCount,
        uint maximumCount,
        string context)
    {
        if (!sections.TryGetValue(type, out TerrainCookedSectionDescriptor descriptor))
        {
            throw Invalid(context, $"required section '{type}' is missing");
        }

        if ((descriptor.Flags & TerrainCookedSectionFlags.Required) == 0 ||
            descriptor.Stride != expectedStride ||
            (expectedCount.HasValue && descriptor.Count != expectedCount.Value) ||
            descriptor.Count > maximumCount)
        {
            throw Invalid(context, $"required section '{type}' has invalid flags, stride, or count");
        }

        return descriptor;
    }

    internal static TerrainCookedSectionDescriptor RequireVariableSection(
        IReadOnlyDictionary<uint, TerrainCookedSectionDescriptor> sections,
        uint type,
        uint maximumCount,
        string context)
    {
        TerrainCookedSectionDescriptor descriptor = RequireSection(
            sections,
            type,
            expectedStride: 0,
            expectedCount: null,
            maximumCount,
            context);
        return descriptor;
    }

    internal static ReadOnlySpan<byte> GetSection(
        ReadOnlySpan<byte> bytes,
        TerrainCookedSectionDescriptor descriptor)
    {
        return bytes.Slice(checked((int)descriptor.Offset), checked((int)descriptor.Size));
    }

    internal static byte[] BuildStringSection(IReadOnlyList<string> strings)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (string value in strings)
        {
            byte[] encoded = s_StrictUtf8.GetBytes(value);
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)encoded.Length));
            stream.Write(length);
            stream.Write(encoded);
        }

        return stream.ToArray();
    }

    internal static string[] ReadStrings(
        ReadOnlySpan<byte> bytes,
        TerrainCookedSectionDescriptor descriptor,
        int maximumStringBytes,
        string context)
    {
        ReadOnlySpan<byte> section = GetSection(bytes, descriptor);
        var strings = new string[descriptor.Count];
        int cursor = 0;
        string? previous = null;
        for (int index = 0; index < strings.Length; index++)
        {
            if (section.Length - cursor < sizeof(uint))
            {
                throw Invalid(context, "string table is truncated before a length field");
            }

            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(section[cursor..]));
            cursor += sizeof(uint);
            if (length <= 0 || length > maximumStringBytes || section.Length - cursor < length)
            {
                throw Invalid(context, $"string table entry '{index}' has invalid byte length '{length}'");
            }

            string value;
            try
            {
                value = s_StrictUtf8.GetString(section.Slice(cursor, length));
            }
            catch (DecoderFallbackException ex)
            {
                throw Invalid(context, $"string table entry '{index}' is not strict UTF-8", ex);
            }

            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Any(char.IsControl) ||
                (previous != null && StringComparer.Ordinal.Compare(previous, value) >= 0))
            {
                throw Invalid(context, $"string table entry '{index}' is empty, noncanonical, or unsorted");
            }

            strings[index] = value;
            previous = value;
            cursor += length;
        }

        if (cursor != section.Length)
        {
            throw Invalid(context, "string table has trailing data");
        }

        return strings;
    }

    internal static string ReadString(
        IReadOnlyList<string> strings,
        uint index,
        string field,
        string context)
    {
        if (index >= strings.Count)
        {
            throw Invalid(context, $"{field} string index '{index}' is outside the string table");
        }

        return strings[checked((int)index)];
    }

    internal static void FinalizeHash(byte[] bytes, int headerSize, int hashOffset)
    {
        SHA256.HashData(bytes.AsSpan(headerSize)).CopyTo(bytes.AsSpan(hashOffset, HashSize));
    }

    internal static void WriteGuid(Span<byte> bytes, Guid guid)
    {
        if (!guid.TryWriteBytes(bytes))
        {
            throw new InvalidOperationException("[TerrainCookedContainer] Failed to serialize a GUID.");
        }
    }

    internal static Guid ReadGuid(ReadOnlySpan<byte> bytes) => new(bytes);

    internal static void EnsureZero(ReadOnlySpan<byte> bytes, string context, string field)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                throw Invalid(context, $"{field} contains nonzero data");
            }
        }
    }

    internal static bool WriteAtomicallyIfChanged(string path, ReadOnlySpan<byte> bytes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            byte[] existing = File.ReadAllBytes(fullPath);
            if (bytes.SequenceEqual(existing))
            {
                return false;
            }
        }

        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return true;
    }

    internal static InvalidDataException Invalid(
        string context,
        string diagnostic,
        Exception? innerException = null)
    {
        return new InvalidDataException(
            $"[TerrainCookedContainer] {context} {diagnostic}.",
            innerException);
    }

    private static void WriteDescriptor(
        Span<byte> bytes,
        TerrainCookedSectionDescriptor descriptor)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, descriptor.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)descriptor.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], descriptor.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], descriptor.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], descriptor.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], descriptor.Stride);
    }

    private static int Align8(int value) => checked((value + 7) & ~7);
}
