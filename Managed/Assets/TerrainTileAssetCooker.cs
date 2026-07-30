using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain.Assets;

internal enum CookedTerrainTileSectionType : uint
{
    Metadata = 1,
    Heights = 2,
    LayerWeights = 3,
    GeometricErrors = 4
}

public static class TerrainTileAssetCooker
{
    public const string RuntimeVariant = "runtime.terrain-tile.v1";
    public const string CookedExtension = ".ariterraintile";
    public const int CookedFormatVersion = 1;

    internal const int HeaderSize = 144;
    internal const int HashOffset = 104;
    internal const int MetadataStride = 96;
    internal const int GeometricErrorStride = 16;

    internal const int MaxCookedTileBytes = 128 * 1024 * 1024;
    private const uint HeightStride = sizeof(ushort);
    private const uint LayerWeightStride = TerrainCookedFormat.WeightChannelCount;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARITTILE");
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedTerrainTileSectionType.Metadata,
        (uint)CookedTerrainTileSectionType.Heights,
        (uint)CookedTerrainTileSectionType.LayerWeights,
        (uint)CookedTerrainTileSectionType.GeometricErrors
    ];

    public static CookedTerrainTileArtifact Cook(
        IAssetDatabase assetDatabase,
        Guid tileGuid,
        string packageId)
    {
        using var zone = Profiler.Zone("Terrain.CookTilePayload");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        string normalizedPackageId = NormalizePackageId(packageId);
        ResolveSource(
            assetDatabase,
            tileGuid,
            normalizedPackageId,
            out TerrainRootSourceDescriptor root,
            out TerrainGeneratedTileRecord tileRecord);
        TerrainLayerSetSourceDescriptor layerSet =
            TerrainLayerSetSourceAssetLoader.LoadSource(assetDatabase, root.LayerSet);
        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(
            root.HeightSource.ResolvedPath);
        TerrainWeightField? weightField = root.WeightSource == null
            ? null
            : TerrainWeightSourceDecoder.DecodeFile(root.WeightSource.ResolvedPath);
        return Cook(assetDatabase, root, layerSet, heightField, tileRecord, weightField);
    }

    internal static CookedTerrainTileArtifact Cook(
        IAssetDatabase assetDatabase,
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainHeightField heightField,
        TerrainGeneratedTileRecord tileRecord,
        TerrainWeightField? weightField = null)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        CookedTerrainTile tile = BuildTile(
            root,
            layerSet,
            heightField,
            tileRecord,
            weightField);
        return Cook(assetDatabase, tile);
    }

    internal static CookedTerrainTileArtifact Cook(
        IAssetDatabase assetDatabase,
        CookedTerrainTile tile)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(tile);
        byte[] payload = WritePayload(tile);
        string outputPath = assetDatabase.GetCookedArtifactPath(
            tile.Guid,
            RuntimeVariant,
            CookedExtension);
        TerrainCookedContainer.WriteAtomicallyIfChanged(outputPath, payload);

        var output = new FileInfo(outputPath);
        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            tile.Guid,
            TerrainAssetTypes.Tile,
            RuntimeVariant,
            output.FullName,
            output.Length,
            output.LastWriteTimeUtc));
        return new CookedTerrainTileArtifact(
            tile.Guid,
            tile.RootGuid,
            tile.Coordinate,
            RuntimeVariant,
            output.FullName,
            output.Length,
            tile.MinHeight,
            tile.MaxHeight,
            SHA256.HashData(payload));
    }

    public static void ValidateSharedBorders(IReadOnlyList<CookedTerrainTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var tilesByCoordinate = new Dictionary<TerrainTileCoordinate, CookedTerrainTile>(tiles.Count);
        foreach (CookedTerrainTile tile in tiles)
        {
            if (!tilesByCoordinate.TryAdd(tile.Coordinate, tile))
            {
                throw new InvalidDataException(
                    $"[TerrainTileAssetCooker] Duplicate cooked tile coordinate {tile.Coordinate}.");
            }
        }

        foreach (CookedTerrainTile tile in tiles)
        {
            if (tilesByCoordinate.TryGetValue(
                    new TerrainTileCoordinate(checked(tile.Coordinate.X + 1), tile.Coordinate.Z),
                    out CookedTerrainTile? positiveX))
            {
                ValidateSharedBorder(tile, positiveX, alongX: true);
            }

            if (tilesByCoordinate.TryGetValue(
                    new TerrainTileCoordinate(tile.Coordinate.X, checked(tile.Coordinate.Z + 1)),
                    out CookedTerrainTile? positiveZ))
            {
                ValidateSharedBorder(tile, positiveZ, alongX: false);
            }
        }
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainTileSourceAsset> tileRef,
        Guid expectedRootGuid,
        Guid expectedLayerSetGuid,
        out CookedTerrainTile tile,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        tile = null!;
        if (!tileRef.IsValid)
        {
            diagnostic = "[TerrainTileAssetCooker] Cooked terrain tile ref is empty.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(tileRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, TerrainAssetTypes.Tile, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[TerrainTileAssetCooker] Terrain tile '{tileRef.Guid:D}' has no cataloged '{TerrainAssetTypes.Tile}' identity.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tileRef.PackageId) &&
            !string.Equals(tileRef.PackageId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[TerrainTileAssetCooker] Terrain tile '{tileRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{tileRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                tileRef.Guid,
                RuntimeVariant,
                TerrainAssetTypes.Tile,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[TerrainTileAssetCooker] Terrain tile '{tileRef.Guid:D}' variant '{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                tileRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{tileRef.Guid:D}:{RuntimeVariant}";
            return TryReadPayload(
                tileRef.Guid,
                expectedRootGuid,
                expectedLayerSetGuid,
                descriptor.PackageId,
                assetDatabase.GetCookedAssetBytes(handle).Span,
                diagnosticPath,
                out tile,
                out diagnostic);
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    public static bool TryReadPayload(
        Guid expectedTileGuid,
        Guid expectedRootGuid,
        Guid expectedLayerSetGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedTerrainTile tile,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Terrain.ReadTilePayload");
        try
        {
            tile = ReadPayload(
                expectedTileGuid,
                expectedRootGuid,
                expectedLayerSetGuid,
                NormalizePackageId(expectedPackageId),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            tile = null!;
            diagnostic =
                $"[TerrainTileAssetCooker] Cooked terrain tile '{diagnosticPath}' is invalid: {ex.Message}";
            return false;
        }
    }

    internal static CookedTerrainTile BuildTile(
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainHeightField heightField,
        TerrainGeneratedTileRecord tileRecord,
        TerrainWeightField? weightField = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(layerSet);
        ArgumentNullException.ThrowIfNull(heightField);
        if (layerSet.Guid != root.LayerSet.Guid ||
            !string.Equals(layerSet.PackageId, root.LayerSet.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "[TerrainTileAssetCooker] Layer set does not match the terrain root reference.");
        }

        if (heightField.Width != root.HeightSource.Width ||
            heightField.Height != root.HeightSource.Height)
        {
            throw new InvalidOperationException(
                "[TerrainTileAssetCooker] Decoded height dimensions changed after root validation.");
        }
        if (weightField != null &&
            (root.WeightSource == null ||
             weightField.Width != heightField.Width ||
             weightField.Height != heightField.Height ||
             root.WeightSource.Width != weightField.Width ||
             root.WeightSource.Height != weightField.Height))
        {
            throw new InvalidOperationException(
                "[TerrainTileAssetCooker] Decoded weight dimensions changed after root validation.");
        }

        Guid expectedGuid = TerrainTileIdentity.CreateGuid(
            root.Guid,
            root.PackageId,
            tileRecord.Coordinate);
        if (tileRecord.Guid != expectedGuid ||
            !root.GeneratedTiles.Contains(tileRecord))
        {
            throw new InvalidOperationException(
                $"[TerrainTileAssetCooker] Tile {tileRecord.Coordinate} has stale or foreign identity.");
        }

        int intervals = root.TileResolution - 1;
        int tileIndexX = checked(tileRecord.Coordinate.X - root.TileOrigin.X);
        int tileIndexZ = checked(tileRecord.Coordinate.Z - root.TileOrigin.Z);
        int tileCountX = (heightField.Width - 1) / intervals;
        int tileCountZ = (heightField.Height - 1) / intervals;
        if ((uint)tileIndexX >= (uint)tileCountX || (uint)tileIndexZ >= (uint)tileCountZ)
        {
            throw new InvalidOperationException(
                $"[TerrainTileAssetCooker] Tile {tileRecord.Coordinate} is outside the root height grid.");
        }

        int sourceOffsetX = checked(tileIndexX * intervals);
        int sourceOffsetZ = checked(tileIndexZ * intervals);
        int sampleCount = checked(root.TileResolution * root.TileResolution);
        var heights = new ushort[sampleCount];
        ushort minimumSample = ushort.MaxValue;
        ushort maximumSample = ushort.MinValue;
        for (int z = 0; z < root.TileResolution; z++)
        {
            for (int x = 0; x < root.TileResolution; x++)
            {
                ushort sample = heightField.GetSample(sourceOffsetX + x, sourceOffsetZ + z);
                heights[checked((z * root.TileResolution) + x)] = sample;
                minimumSample = Math.Min(minimumSample, sample);
                maximumSample = Math.Max(maximumSample, sample);
            }
        }

        double minimumHeight = DecodeHeight(root.HeightRange, minimumSample);
        double maximumHeight = DecodeHeight(root.HeightRange, maximumSample);
        var weights = new byte[checked(sampleCount * TerrainCookedFormat.WeightChannelCount)];
        for (int z = 0; z < root.TileResolution; z++)
        {
            for (int x = 0; x < root.TileResolution; x++)
            {
                int sampleIndex = checked((z * root.TileResolution) + x);
                Span<byte> destination = weights.AsSpan(
                    sampleIndex * TerrainCookedFormat.WeightChannelCount,
                    TerrainCookedFormat.WeightChannelCount);
                if (weightField == null)
                {
                    destination[0] = byte.MaxValue;
                    continue;
                }

                NormalizeLayerWeights(
                    weightField.GetSample(sourceOffsetX + x, sourceOffsetZ + z),
                    layerSet.Layers.Count,
                    destination,
                    $"terrain root '{root.Guid:D}' sample ({sourceOffsetX + x}, {sourceOffsetZ + z})");
            }
        }

        TerrainGeometricErrorLevel[] errors = BuildGeometricErrors(
            heights,
            root.TileResolution,
            root.HeightRange);
        var placement = new WorldPosition(
            root.WorldPlacement.X + (sourceOffsetX * root.SampleSpacing.X),
            root.WorldPlacement.Y,
            root.WorldPlacement.Z + (sourceOffsetZ * root.SampleSpacing.Z));
        if (!placement.IsFinite)
        {
            throw new InvalidOperationException(
                "[TerrainTileAssetCooker] Tile world placement is non-finite.");
        }

        return new CookedTerrainTile(
            tileRecord.Guid,
            root.Guid,
            layerSet.Guid,
            root.PackageId,
            root.SourceSchemaVersion,
            tileRecord.Coordinate,
            root.TileResolution,
            layerSet.Layers.Count,
            placement,
            root.SampleSpacing,
            root.HeightRange,
            minimumHeight,
            maximumHeight,
            root.BorderPolicy,
            sourceOffsetX,
            sourceOffsetZ,
            heights,
            weights,
            errors);
    }

    internal static byte[] WritePayload(CookedTerrainTile tile)
    {
        ValidateTileForWrite(tile);
        byte[] metadata = BuildMetadataSection(tile);
        byte[] heightBytes = new byte[checked(tile.Heights.Length * sizeof(ushort))];
        ReadOnlySpan<ushort> heights = tile.Heights.Span;
        for (int index = 0; index < heights.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                heightBytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                heights[index]);
        }

        byte[] errorBytes = new byte[checked(tile.GeometricErrors.Count * GeometricErrorStride)];
        for (int index = 0; index < tile.GeometricErrors.Count; index++)
        {
            TerrainGeometricErrorLevel error = tile.GeometricErrors[index];
            Span<byte> record = errorBytes.AsSpan(index * GeometricErrorStride, GeometricErrorStride);
            BinaryPrimitives.WriteInt32LittleEndian(record, error.Level);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], error.SampleStep);
            BinaryPrimitives.WriteDoubleLittleEndian(record[8..], error.MaxError);
        }

        TerrainCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedTerrainTileSectionType.Metadata,
                TerrainCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedTerrainTileSectionType.Heights,
                TerrainCookedSectionFlags.Required,
                checked((uint)tile.Heights.Length),
                HeightStride,
                heightBytes),
            new(
                (uint)CookedTerrainTileSectionType.LayerWeights,
                TerrainCookedSectionFlags.Required,
                checked((uint)tile.Heights.Length),
                LayerWeightStride,
                tile.LayerWeights.ToArray()),
            new(
                (uint)CookedTerrainTileSectionType.GeometricErrors,
                TerrainCookedSectionFlags.Required,
                checked((uint)tile.GeometricErrors.Count),
                GeometricErrorStride,
                errorBytes)
        ];
        byte[] output = TerrainCookedContainer.Build(
            HeaderSize,
            MaxCookedTileBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], TerrainCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], tile.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        TerrainCookedContainer.WriteGuid(header[24..40], tile.Guid);
        TerrainCookedContainer.WriteGuid(header[40..56], tile.RootGuid);
        TerrainCookedContainer.WriteGuid(header[56..72], tile.LayerSetGuid);
        BinaryPrimitives.WriteInt32LittleEndian(header[72..], tile.Coordinate.X);
        BinaryPrimitives.WriteInt32LittleEndian(header[76..], tile.Coordinate.Z);
        BinaryPrimitives.WriteInt32LittleEndian(header[80..], tile.Resolution);
        BinaryPrimitives.WriteInt32LittleEndian(header[84..], tile.LayerCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[88..], sections.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[96..], checked((ulong)output.Length));
        TerrainCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static TerrainGeometricErrorLevel[] BuildGeometricErrors(
        ReadOnlySpan<ushort> heights,
        int resolution,
        TerrainHeightRange heightRange)
    {
        int intervals = resolution - 1;
        int levelCount = BitOperations.Log2(checked((uint)intervals)) + 1;
        var errors = new TerrainGeometricErrorLevel[levelCount];
        double previousError = 0.0;
        for (int level = 0; level < levelCount; level++)
        {
            int step = 1 << level;
            double maximumError = 0.0;
            if (step > 1)
            {
                for (int blockZ = 0; blockZ < intervals; blockZ += step)
                {
                    for (int blockX = 0; blockX < intervals; blockX += step)
                    {
                        double h00 = DecodeHeight(heightRange, heights[(blockZ * resolution) + blockX]);
                        double h10 = DecodeHeight(heightRange, heights[(blockZ * resolution) + blockX + step]);
                        double h01 = DecodeHeight(heightRange, heights[((blockZ + step) * resolution) + blockX]);
                        double h11 = DecodeHeight(heightRange, heights[((blockZ + step) * resolution) + blockX + step]);
                        for (int localZ = 0; localZ <= step; localZ++)
                        {
                            double tz = (double)localZ / step;
                            double left = h00 + ((h01 - h00) * tz);
                            double right = h10 + ((h11 - h10) * tz);
                            for (int localX = 0; localX <= step; localX++)
                            {
                                double tx = (double)localX / step;
                                double interpolated = left + ((right - left) * tx);
                                double actual = DecodeHeight(
                                    heightRange,
                                    heights[((blockZ + localZ) * resolution) + blockX + localX]);
                                maximumError = Math.Max(maximumError, Math.Abs(actual - interpolated));
                            }
                        }
                    }
                }
            }

            maximumError = Math.Max(previousError, maximumError);
            errors[level] = new TerrainGeometricErrorLevel(level, step, maximumError);
            previousError = maximumError;
        }

        return errors;
    }

    private static CookedTerrainTile ReadPayload(
        Guid expectedTileGuid,
        Guid expectedRootGuid,
        Guid expectedLayerSetGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"terrain tile '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw TerrainCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw TerrainCookedContainer.Invalid(context, "magic is not ARITTILE");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int sourceVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid tileGuid = TerrainCookedContainer.ReadGuid(bytes[24..40]);
        Guid rootGuid = TerrainCookedContainer.ReadGuid(bytes[40..56]);
        Guid layerSetGuid = TerrainCookedContainer.ReadGuid(bytes[56..72]);
        var coordinate = new TerrainTileCoordinate(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[72..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[76..]));
        int resolution = BinaryPrimitives.ReadInt32LittleEndian(bytes[80..]);
        int layerCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[84..]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[88..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[92..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..]);

        if (endian != TerrainCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            sourceVersion is < TerrainRootSourceAssetLoader.MinimumSourceSchemaVersion or
                > TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion ||
            headerSize != HeaderSize ||
            tileGuid == Guid.Empty || tileGuid != expectedTileGuid ||
            rootGuid == Guid.Empty || (expectedRootGuid != Guid.Empty && rootGuid != expectedRootGuid) ||
            layerSetGuid == Guid.Empty ||
            (expectedLayerSetGuid != Guid.Empty && layerSetGuid != expectedLayerSetGuid) ||
            layerCount is < 1 or > TerrainLayerSetSourceAssetLoader.MaxLayerCount ||
            reserved != 0 ||
            declaredSize != checked((ulong)bytes.Length))
        {
            throw TerrainCookedContainer.Invalid(context, "header identity, version, dimensions, or size is invalid");
        }

        ValidateResolution(resolution, context);
        TerrainTileIdentity.ValidateCoordinate(coordinate);
        Guid deterministicGuid = TerrainTileIdentity.CreateGuid(rootGuid, expectedPackageId, coordinate);
        if (deterministicGuid != tileGuid)
        {
            throw TerrainCookedContainer.Invalid(
                context,
                $"tile GUID '{tileGuid:D}' does not match deterministic coordinate identity '{deterministicGuid:D}'");
        }

        TerrainCookedContainer.EnsureZero(bytes[136..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, TerrainCookedSectionDescriptor> sections =
            TerrainCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedTileBytes,
                s_KnownSections,
                context);
        uint sampleCount = checked((uint)(resolution * resolution));
        TerrainCookedSectionDescriptor metadataSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainTileSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        TerrainCookedSectionDescriptor heightsSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainTileSectionType.Heights,
            HeightStride,
            sampleCount,
            sampleCount,
            context);
        TerrainCookedSectionDescriptor weightsSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainTileSectionType.LayerWeights,
            LayerWeightStride,
            sampleCount,
            sampleCount,
            context);
        int expectedErrorCount = BitOperations.Log2(checked((uint)(resolution - 1))) + 1;
        TerrainCookedSectionDescriptor errorsSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainTileSectionType.GeometricErrors,
            GeometricErrorStride,
            checked((uint)expectedErrorCount),
            checked((uint)expectedErrorCount),
            context);

        ReadOnlySpan<byte> metadata = TerrainCookedContainer.GetSection(bytes, metadataSection);
        var placement = new WorldPosition(
            BinaryPrimitives.ReadDoubleLittleEndian(metadata),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[16..]));
        var spacing = new TerrainSampleSpacing(
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[24..]),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[32..]));
        double heightOffset = BinaryPrimitives.ReadDoubleLittleEndian(metadata[40..]);
        double heightScale = BinaryPrimitives.ReadDoubleLittleEndian(metadata[48..]);
        var heightRange = new TerrainHeightRange(heightOffset, heightOffset + heightScale);
        double minimumHeight = BinaryPrimitives.ReadDoubleLittleEndian(metadata[56..]);
        double maximumHeight = BinaryPrimitives.ReadDoubleLittleEndian(metadata[64..]);
        uint declaredSampleCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[72..]);
        uint rawBorderPolicy = BinaryPrimitives.ReadUInt32LittleEndian(metadata[76..]);
        uint declaredErrorCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[80..]);
        uint weightChannelCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[84..]);
        int sourceOffsetX = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata[88..]));
        int sourceOffsetZ = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata[92..]));
        if (!placement.IsFinite ||
            !spacing.IsValid ||
            !heightRange.IsValid ||
            !double.IsFinite(minimumHeight) ||
            !double.IsFinite(maximumHeight) ||
            minimumHeight > maximumHeight ||
            minimumHeight < heightRange.Min ||
            maximumHeight > heightRange.Max ||
            declaredSampleCount != sampleCount ||
            rawBorderPolicy != (uint)TerrainBorderPolicy.SharedEdgeSamples ||
            declaredErrorCount != expectedErrorCount ||
            weightChannelCount != TerrainCookedFormat.WeightChannelCount ||
            sourceOffsetX > TerrainHeightSourceDecoder.MaxDimension ||
            sourceOffsetZ > TerrainHeightSourceDecoder.MaxDimension)
        {
            throw TerrainCookedContainer.Invalid(context, "metadata is non-finite, unsupported, or inconsistent");
        }

        ReadOnlySpan<byte> encodedHeights = TerrainCookedContainer.GetSection(bytes, heightsSection);
        var heights = new ushort[sampleCount];
        ushort minimumSample = ushort.MaxValue;
        ushort maximumSample = ushort.MinValue;
        for (int index = 0; index < heights.Length; index++)
        {
            ushort sample = BinaryPrimitives.ReadUInt16LittleEndian(
                encodedHeights.Slice(index * sizeof(ushort), sizeof(ushort)));
            heights[index] = sample;
            minimumSample = Math.Min(minimumSample, sample);
            maximumSample = Math.Max(maximumSample, sample);
        }

        double computedMinimum = DecodeHeight(heightRange, minimumSample);
        double computedMaximum = DecodeHeight(heightRange, maximumSample);
        if (BitConverter.DoubleToInt64Bits(computedMinimum) != BitConverter.DoubleToInt64Bits(minimumHeight) ||
            BitConverter.DoubleToInt64Bits(computedMaximum) != BitConverter.DoubleToInt64Bits(maximumHeight))
        {
            throw TerrainCookedContainer.Invalid(context, "declared min/max heights do not match quantized samples");
        }

        byte[] weights = TerrainCookedContainer.GetSection(bytes, weightsSection).ToArray();
        ValidateWeights(weights, layerCount, context);

        TerrainGeometricErrorLevel[] expectedErrors = BuildGeometricErrors(
            heights,
            resolution,
            heightRange);
        ReadOnlySpan<byte> encodedErrors = TerrainCookedContainer.GetSection(bytes, errorsSection);
        var errors = new TerrainGeometricErrorLevel[expectedErrorCount];
        double previousError = -1.0;
        for (int index = 0; index < errors.Length; index++)
        {
            ReadOnlySpan<byte> record = encodedErrors.Slice(
                index * GeometricErrorStride,
                GeometricErrorStride);
            int level = BinaryPrimitives.ReadInt32LittleEndian(record);
            int sampleStep = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            double maxError = BinaryPrimitives.ReadDoubleLittleEndian(record[8..]);
            TerrainGeometricErrorLevel expected = expectedErrors[index];
            if (level != index ||
                sampleStep != (1 << index) ||
                !double.IsFinite(maxError) ||
                maxError < 0.0 ||
                maxError < previousError ||
                BitConverter.DoubleToInt64Bits(maxError) !=
                BitConverter.DoubleToInt64Bits(expected.MaxError))
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"geometric-error level '{index}' is noncanonical or does not match the height samples");
            }

            errors[index] = new TerrainGeometricErrorLevel(level, sampleStep, maxError);
            previousError = maxError;
        }

        return new CookedTerrainTile(
            tileGuid,
            rootGuid,
            layerSetGuid,
            expectedPackageId,
            sourceVersion,
            coordinate,
            resolution,
            layerCount,
            placement,
            spacing,
            heightRange,
            minimumHeight,
            maximumHeight,
            TerrainBorderPolicy.SharedEdgeSamples,
            sourceOffsetX,
            sourceOffsetZ,
            heights,
            weights,
            errors);
    }

    private static byte[] BuildMetadataSection(CookedTerrainTile tile)
    {
        byte[] bytes = new byte[MetadataStride];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(0), tile.WorldPlacement.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8), tile.WorldPlacement.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(16), tile.WorldPlacement.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(24), tile.SampleSpacing.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(32), tile.SampleSpacing.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(40), tile.HeightRange.Min);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(48), tile.HeightRange.Scale);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(56), tile.MinHeight);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(64), tile.MaxHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), checked((uint)tile.Heights.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), (uint)tile.BorderPolicy);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), checked((uint)tile.GeometricErrors.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), TerrainCookedFormat.WeightChannelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88), checked((uint)tile.SourceSampleOffsetX));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), checked((uint)tile.SourceSampleOffsetZ));
        return bytes;
    }

    private static void ValidateTileForWrite(CookedTerrainTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        string context = $"terrain tile '{tile.Guid:D}'";
        if (tile.Guid == Guid.Empty ||
            tile.RootGuid == Guid.Empty ||
            tile.LayerSetGuid == Guid.Empty ||
            tile.SourceSchemaVersion is < TerrainRootSourceAssetLoader.MinimumSourceSchemaVersion or
                > TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion ||
            tile.LayerCount is < 1 or > TerrainLayerSetSourceAssetLoader.MaxLayerCount ||
            !tile.WorldPlacement.IsFinite ||
            !tile.SampleSpacing.IsValid ||
            !tile.HeightRange.IsValid ||
            !double.IsFinite(tile.MinHeight) ||
            !double.IsFinite(tile.MaxHeight) ||
            tile.MinHeight > tile.MaxHeight ||
            tile.BorderPolicy != TerrainBorderPolicy.SharedEdgeSamples)
        {
            throw new InvalidOperationException(
                $"[TerrainTileAssetCooker] {context} has invalid identity or metadata.");
        }

        ValidateResolution(tile.Resolution, context);
        int sampleCount = checked(tile.Resolution * tile.Resolution);
        if (tile.Heights.Length != sampleCount ||
            tile.LayerWeights.Length != checked(sampleCount * TerrainCookedFormat.WeightChannelCount))
        {
            throw new InvalidOperationException(
                $"[TerrainTileAssetCooker] {context} has inconsistent sample payload lengths.");
        }

        ValidateWeights(tile.LayerWeights.Span, tile.LayerCount, context);
        TerrainGeometricErrorLevel[] expected = BuildGeometricErrors(
            tile.Heights.Span,
            tile.Resolution,
            tile.HeightRange);
        if (!tile.GeometricErrors.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"[TerrainTileAssetCooker] {context} has noncanonical geometric errors.");
        }
    }

    private static void ValidateWeights(
        ReadOnlySpan<byte> weights,
        int layerCount,
        string context)
    {
        for (int offset = 0; offset < weights.Length; offset += TerrainCookedFormat.WeightChannelCount)
        {
            int sum = 0;
            for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
            {
                byte weight = weights[offset + channel];
                if (channel >= layerCount && weight != 0)
                {
                    throw TerrainCookedContainer.Invalid(
                        context,
                        $"sample '{offset / TerrainCookedFormat.WeightChannelCount}' uses inactive layer channel '{channel}'");
                }

                sum += weight;
            }

            if (sum != byte.MaxValue)
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"sample '{offset / TerrainCookedFormat.WeightChannelCount}' layer weights sum to '{sum}', expected 255");
            }
        }
    }

    internal static void NormalizeLayerWeights(
        ReadOnlySpan<byte> source,
        int layerCount,
        Span<byte> destination,
        string context)
    {
        if (source.Length != TerrainCookedFormat.WeightChannelCount ||
            destination.Length != TerrainCookedFormat.WeightChannelCount ||
            layerCount is < 1 or > TerrainCookedFormat.WeightChannelCount)
        {
            throw new ArgumentException(
                "Terrain weight normalization requires four channels and a bounded layer count.");
        }

        destination.Clear();
        int sum = 0;
        for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
        {
            if (channel >= layerCount && source[channel] != 0)
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"authored weight uses inactive layer channel '{channel}'");
            }
            if (channel < layerCount)
            {
                sum += source[channel];
            }
        }

        if (sum == 0)
        {
            destination[0] = byte.MaxValue;
            return;
        }

        Span<int> remainders = stackalloc int[TerrainCookedFormat.WeightChannelCount];
        int assigned = 0;
        for (int channel = 0; channel < layerCount; channel++)
        {
            int scaled = source[channel] * byte.MaxValue;
            int value = scaled / sum;
            destination[channel] = checked((byte)value);
            remainders[channel] = scaled % sum;
            assigned += value;
        }

        int remaining = byte.MaxValue - assigned;
        while (remaining-- > 0)
        {
            int selected = 0;
            for (int channel = 1; channel < layerCount; channel++)
            {
                if (remainders[channel] > remainders[selected])
                {
                    selected = channel;
                }
            }

            destination[selected]++;
            remainders[selected] = -1;
        }
    }

    private static void ValidateSharedBorder(
        CookedTerrainTile lower,
        CookedTerrainTile upper,
        bool alongX)
    {
        if (lower.RootGuid != upper.RootGuid ||
            lower.LayerSetGuid != upper.LayerSetGuid ||
            !string.Equals(lower.PackageId, upper.PackageId, StringComparison.Ordinal) ||
            lower.Resolution != upper.Resolution ||
            lower.LayerCount != upper.LayerCount ||
            lower.SampleSpacing != upper.SampleSpacing ||
            lower.HeightRange != upper.HeightRange ||
            lower.BorderPolicy != TerrainBorderPolicy.SharedEdgeSamples ||
            upper.BorderPolicy != TerrainBorderPolicy.SharedEdgeSamples)
        {
            throw new InvalidDataException(
                $"[TerrainTileAssetCooker] Adjacent tiles {lower.Coordinate} and {upper.Coordinate} " +
                "do not share compatible root, layer, or sampling metadata.");
        }

        int intervals = lower.Resolution - 1;
        WorldPosition expectedPlacement = alongX
            ? new WorldPosition(
                lower.WorldPlacement.X + (intervals * lower.SampleSpacing.X),
                lower.WorldPlacement.Y,
                lower.WorldPlacement.Z)
            : new WorldPosition(
                lower.WorldPlacement.X,
                lower.WorldPlacement.Y,
                lower.WorldPlacement.Z + (intervals * lower.SampleSpacing.Z));
        if (upper.WorldPlacement != expectedPlacement)
        {
            throw new InvalidDataException(
                $"[TerrainTileAssetCooker] Adjacent tile {upper.Coordinate} has world placement " +
                $"{upper.WorldPlacement}, expected {expectedPlacement}.");
        }

        for (int sample = 0; sample < lower.Resolution; sample++)
        {
            ushort lowerHeight = alongX
                ? lower.GetHeightSample(lower.Resolution - 1, sample)
                : lower.GetHeightSample(sample, lower.Resolution - 1);
            ushort upperHeight = alongX
                ? upper.GetHeightSample(0, sample)
                : upper.GetHeightSample(sample, 0);
            if (lowerHeight != upperHeight)
            {
                throw new InvalidDataException(
                    $"[TerrainTileAssetCooker] Adjacent tiles {lower.Coordinate} and " +
                    $"{upper.Coordinate} disagree at shared-border sample '{sample}'.");
            }

            for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
            {
                byte lowerWeight = alongX
                    ? lower.GetLayerWeight(lower.Resolution - 1, sample, channel)
                    : lower.GetLayerWeight(sample, lower.Resolution - 1, channel);
                byte upperWeight = alongX
                    ? upper.GetLayerWeight(0, sample, channel)
                    : upper.GetLayerWeight(sample, 0, channel);
                if (lowerWeight != upperWeight)
                {
                    throw new InvalidDataException(
                        $"[TerrainTileAssetCooker] Adjacent tiles {lower.Coordinate} and " +
                        $"{upper.Coordinate} disagree at shared-border weight sample '{sample}', channel '{channel}'.");
                }
            }
        }
    }

    private static void ResolveSource(
        IAssetDatabase assetDatabase,
        Guid tileGuid,
        string packageId,
        out TerrainRootSourceDescriptor root,
        out TerrainGeneratedTileRecord tileRecord)
    {
        if (tileGuid == Guid.Empty)
        {
            throw new ArgumentException("Terrain tile cooking requires a stable tile GUID.", nameof(tileGuid));
        }

        foreach (AssetRecord rootAsset in assetDatabase.Assets
                     .Where(asset =>
                         string.Equals(asset.AssetType, TerrainAssetTypes.Root, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(asset.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(asset => asset.Guid))
        {
            TerrainRootSourceDescriptor candidate = TerrainRootSourceAssetLoader.LoadSource(rootAsset);
            foreach (TerrainGeneratedTileRecord record in candidate.GeneratedTiles)
            {
                if (record.Guid == tileGuid)
                {
                    root = candidate;
                    tileRecord = record;
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            $"[TerrainTileAssetCooker] Generated terrain tile '{tileGuid:D}' is not owned by package '{packageId}'.");
    }

    private static void ValidateResolution(int resolution, string context)
    {
        int intervals = resolution - 1;
        if (resolution < TerrainRootSourceAssetLoader.MinTileResolution ||
            resolution > TerrainRootSourceAssetLoader.MaxTileResolution ||
            (intervals & (intervals - 1)) != 0)
        {
            throw TerrainCookedContainer.Invalid(context, $"tile resolution '{resolution}' is not supported");
        }
    }

    private static double DecodeHeight(TerrainHeightRange range, ushort value)
    {
        return range.Min + ((double)value / ushort.MaxValue * range.Scale);
    }

    private static string NormalizePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) ||
            !string.Equals(packageId, packageId.Trim(), StringComparison.Ordinal) ||
            packageId.Any(char.IsControl))
        {
            throw new ArgumentException("Terrain package id must be non-empty canonical text.", nameof(packageId));
        }

        return packageId.ToLowerInvariant();
    }
}
