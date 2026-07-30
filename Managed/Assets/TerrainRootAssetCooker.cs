using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain.Assets;

internal enum CookedTerrainRootSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Layers = 3,
    Tiles = 4
}

public sealed record TerrainIncrementalCookResult(
    CookedTerrainRootArtifact RootArtifact,
    IReadOnlyList<TerrainTileCoordinate> RequestedTiles,
    IReadOnlyList<TerrainTileCoordinate> RecookedTiles,
    IReadOnlyList<TerrainTileCoordinate> DependencyRecookedTiles,
    IReadOnlyList<TerrainTileCoordinate> ReusedTiles);

public static class TerrainRootAssetCooker
{
    public const string RuntimeVariant = "runtime.terrain-root.v2";
    public const string CookedExtension = ".ariterrainroot";
    public const int CookedFormatVersion = 2;

    internal const int HeaderSize = 128;
    internal const int HashOffset = 80;
    internal const int MetadataStride = 96;
    internal const int LayerStride = 128;
    internal const int TileStride = 144;

    private const int MaxCookedRootBytes = 64 * 1024 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const string TextureAssetType = "Texture2D";
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARITROOT");
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedTerrainRootSectionType.Metadata,
        (uint)CookedTerrainRootSectionType.Strings,
        (uint)CookedTerrainRootSectionType.Layers,
        (uint)CookedTerrainRootSectionType.Tiles
    ];

    public static CookedTerrainRootArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef)
    {
        CookedTerrainRoot? previousRoot = TryLoadPreviousCookedRoot(assetDatabase, rootRef);
        return Cook(assetDatabase, rootRef, previousRoot);
    }

    internal static CookedTerrainRootArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef,
        CookedTerrainRoot? previousRoot)
    {
        using var zone = Profiler.Zone("Terrain.CookRootPayload");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        TerrainRootSourceDescriptor root = TerrainRootSourceAssetLoader.LoadSource(
            assetDatabase,
            rootRef);
        TerrainLayerSetSourceDescriptor layerSet =
            TerrainLayerSetSourceAssetLoader.LoadSource(assetDatabase, root.LayerSet);
        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(
            root.HeightSource.ResolvedPath);
        TerrainWeightField? weightField = root.WeightSource == null
            ? null
            : TerrainWeightSourceDecoder.DecodeFile(root.WeightSource.ResolvedPath);

        var tiles = new CookedTerrainTile[root.GeneratedTiles.Count];
        for (int index = 0; index < root.GeneratedTiles.Count; index++)
        {
            tiles[index] = TerrainTileAssetCooker.BuildTile(
                root,
                layerSet,
                heightField,
                root.GeneratedTiles[index],
                weightField);
        }

        TerrainTileAssetCooker.ValidateSharedBorders(tiles);
        var tileArtifacts = new CookedTerrainTileArtifact[tiles.Length];
        for (int index = 0; index < tiles.Length; index++)
        {
            tileArtifacts[index] = TerrainTileAssetCooker.Cook(assetDatabase, tiles[index]);
        }

        return WriteRootArtifact(
            assetDatabase,
            root,
            layerSet,
            tileArtifacts,
            previousRoot);
    }

    public static TerrainIncrementalCookResult CookChangedTiles(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef,
        IReadOnlyCollection<TerrainTileCoordinate> changedTiles)
    {
        using var zone = Profiler.Zone("Terrain.CookChangedTiles");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(changedTiles);
        if (!assetDatabase.CanReadSourceAssets)
        {
            throw new InvalidOperationException(
                "Incremental terrain cooking requires source asset access.");
        }

        TerrainRootSourceDescriptor root = TerrainRootSourceAssetLoader.LoadSource(
            assetDatabase,
            rootRef);
        TerrainLayerSetSourceDescriptor layerSet =
            TerrainLayerSetSourceAssetLoader.LoadSource(assetDatabase, root.LayerSet);
        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(
            root.HeightSource.ResolvedPath);
        TerrainWeightField? weightField = root.WeightSource == null
            ? null
            : TerrainWeightSourceDecoder.DecodeFile(root.WeightSource.ResolvedPath);
        var requested = new HashSet<TerrainTileCoordinate>(changedTiles);
        var sourceCoordinates = root.GeneratedTiles
            .Select(record => record.Coordinate)
            .ToHashSet();
        if (!requested.IsSubsetOf(sourceCoordinates))
        {
            throw new InvalidOperationException(
                "Incremental terrain cook contains a tile outside the terrain root grid.");
        }

        CookedTerrainRoot? previousRoot = TryLoadPreviousCookedRoot(
            assetDatabase,
            rootRef);
        var tiles = new CookedTerrainTile[root.GeneratedTiles.Count];
        var artifacts = new CookedTerrainTileArtifact[root.GeneratedTiles.Count];
        var recooked = new List<TerrainTileCoordinate>();
        var dependencyRecooked = new List<TerrainTileCoordinate>();
        var reused = new List<TerrainTileCoordinate>();
        for (int index = 0; index < root.GeneratedTiles.Count; index++)
        {
            TerrainGeneratedTileRecord record = root.GeneratedTiles[index];
            bool wasRequested = requested.Contains(record.Coordinate);
            if (!wasRequested && TryReuseTileArtifact(
                    assetDatabase,
                    root,
                    layerSet,
                    record,
                    out CookedTerrainTile reusedTile,
                    out CookedTerrainTileArtifact reusedArtifact))
            {
                tiles[index] = reusedTile;
                artifacts[index] = reusedArtifact;
                reused.Add(record.Coordinate);
                continue;
            }

            CookedTerrainTile tile = TerrainTileAssetCooker.BuildTile(
                root,
                layerSet,
                heightField,
                record,
                weightField);
            tiles[index] = tile;
            artifacts[index] = TerrainTileAssetCooker.Cook(assetDatabase, tile);
            recooked.Add(record.Coordinate);
            if (!wasRequested)
            {
                dependencyRecooked.Add(record.Coordinate);
            }
        }

        TerrainTileAssetCooker.ValidateSharedBorders(tiles);
        CookedTerrainRootArtifact rootArtifact = WriteRootArtifact(
            assetDatabase,
            root,
            layerSet,
            artifacts,
            previousRoot);
        TerrainTileCoordinate[] orderedRequested = requested.ToArray();
        Array.Sort(orderedRequested);
        return new TerrainIncrementalCookResult(
            rootArtifact,
            Array.AsReadOnly(orderedRequested),
            Array.AsReadOnly(recooked.ToArray()),
            Array.AsReadOnly(dependencyRecooked.ToArray()),
            Array.AsReadOnly(reused.ToArray()));
    }

    private static bool TryReuseTileArtifact(
        IAssetDatabase assetDatabase,
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainGeneratedTileRecord record,
        out CookedTerrainTile tile,
        out CookedTerrainTileArtifact artifact)
    {
        var tileRef = new AssetRef<TerrainTileSourceAsset>(
            record.Guid,
            TerrainAssetTypes.Tile,
            root.PackageId);
        if (!TerrainTileAssetCooker.TryLoadCooked(
                assetDatabase,
                tileRef,
                root.Guid,
                layerSet.Guid,
                out tile,
                out _) ||
            !IsReusableTile(tile, root, layerSet, record) ||
            !assetDatabase.TryGetCookedArtifact(
                record.Guid,
                TerrainTileAssetCooker.RuntimeVariant,
                out CookedAssetRecord? cookedRecord) ||
            !File.Exists(cookedRecord.Path))
        {
            tile = null!;
            artifact = null!;
            return false;
        }

        var file = new FileInfo(cookedRecord.Path);
        byte[] contentHash;
        using (var stream = new FileStream(
                   file.FullName,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        {
            contentHash = SHA256.HashData(stream);
        }

        artifact = new CookedTerrainTileArtifact(
            tile.Guid,
            tile.RootGuid,
            tile.Coordinate,
            TerrainTileAssetCooker.RuntimeVariant,
            file.FullName,
            file.Length,
            tile.MinHeight,
            tile.MaxHeight,
            contentHash);
        return true;
    }

    private static bool IsReusableTile(
        CookedTerrainTile tile,
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainGeneratedTileRecord record)
    {
        int intervals = root.TileResolution - 1;
        int localX = checked(record.Coordinate.X - root.TileOrigin.X);
        int localZ = checked(record.Coordinate.Z - root.TileOrigin.Z);
        int sourceOffsetX = checked(localX * intervals);
        int sourceOffsetZ = checked(localZ * intervals);
        var expectedPlacement = new WorldPosition(
            root.WorldPlacement.X + (sourceOffsetX * root.SampleSpacing.X),
            root.WorldPlacement.Y,
            root.WorldPlacement.Z + (sourceOffsetZ * root.SampleSpacing.Z));
        return tile.Guid == record.Guid &&
               tile.RootGuid == root.Guid &&
               tile.LayerSetGuid == layerSet.Guid &&
               string.Equals(tile.PackageId, root.PackageId, StringComparison.Ordinal) &&
               tile.SourceSchemaVersion == root.SourceSchemaVersion &&
               tile.Coordinate == record.Coordinate &&
               tile.Resolution == root.TileResolution &&
               tile.LayerCount == layerSet.Layers.Count &&
               tile.WorldPlacement == expectedPlacement &&
               tile.SampleSpacing == root.SampleSpacing &&
               tile.HeightRange == root.HeightRange &&
               tile.BorderPolicy == root.BorderPolicy &&
               tile.SourceSampleOffsetX == sourceOffsetX &&
               tile.SourceSampleOffsetZ == sourceOffsetZ;
    }

    private static CookedTerrainRootArtifact WriteRootArtifact(
        IAssetDatabase assetDatabase,
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        IReadOnlyList<CookedTerrainTileArtifact> tileArtifacts,
        CookedTerrainRoot? previousRoot)
    {
        CookedTerrainRoot cookedRoot = BuildRoot(root, layerSet, tileArtifacts);
        byte[] payload = WritePayload(cookedRoot);
        string outputPath = assetDatabase.GetCookedArtifactPath(
            root.Guid,
            RuntimeVariant,
            CookedExtension);
        TerrainCookedContainer.WriteAtomicallyIfChanged(outputPath, payload);

        var output = new FileInfo(outputPath);
        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            root.Guid,
            TerrainAssetTypes.Root,
            RuntimeVariant,
            output.FullName,
            output.Length,
            output.LastWriteTimeUtc));
        RemoveStaleGeneratedTiles(assetDatabase, previousRoot, cookedRoot);
        TerrainCookedAssetDependency[] dependencies = BuildDependencies(cookedRoot);
        return new CookedTerrainRootArtifact(
            root.Guid,
            RuntimeVariant,
            output.FullName,
            cookedRoot.Tiles.Count,
            cookedRoot.Layers.Count,
            output.Length,
            dependencies);
    }

    internal static CookedTerrainRoot? TryLoadPreviousCookedRoot(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!assetDatabase.TryGetCookedArtifact(rootRef.Guid, RuntimeVariant, out _))
        {
            return null;
        }

        return TryLoadCooked(assetDatabase, rootRef, out CookedTerrainRoot previousRoot, out _)
            ? previousRoot
            : null;
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef,
        out CookedTerrainRoot root,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        root = null!;
        if (!rootRef.IsValid)
        {
            diagnostic = "[TerrainRootAssetCooker] Cooked terrain root ref is empty.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(rootRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, TerrainAssetTypes.Root, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[TerrainRootAssetCooker] Terrain root '{rootRef.Guid:D}' has no cataloged '{TerrainAssetTypes.Root}' identity.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rootRef.PackageId) &&
            !string.Equals(rootRef.PackageId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[TerrainRootAssetCooker] Terrain root '{rootRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{rootRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                rootRef.Guid,
                RuntimeVariant,
                TerrainAssetTypes.Root,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[TerrainRootAssetCooker] Terrain root '{rootRef.Guid:D}' variant '{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                rootRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{rootRef.Guid:D}:{RuntimeVariant}";
            return TryReadPayload(
                rootRef.Guid,
                descriptor.PackageId,
                assetDatabase.GetCookedAssetBytes(handle).Span,
                diagnosticPath,
                out root,
                out diagnostic);
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    public static bool TryReadPayload(
        Guid expectedRootGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedTerrainRoot root,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Terrain.ReadRootPayload");
        try
        {
            root = ReadPayload(
                expectedRootGuid,
                NormalizePackageId(expectedPackageId),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            root = null!;
            diagnostic =
                $"[TerrainRootAssetCooker] Cooked terrain root '{diagnosticPath}' is invalid: {ex.Message}";
            return false;
        }
    }

    private static void RemoveStaleGeneratedTiles(
        IAssetDatabase assetDatabase,
        CookedTerrainRoot? previousRoot,
        CookedTerrainRoot currentRoot)
    {
        if (previousRoot == null)
        {
            return;
        }

        var currentTileGuids = currentRoot.Tiles
            .Select(tile => tile.Guid)
            .ToHashSet();
        CookedAssetIdentity[] staleTiles = previousRoot.Tiles
            .Where(tile => !currentTileGuids.Contains(tile.Guid))
            .Select(tile => new CookedAssetIdentity(
                tile.Guid,
                TerrainTileAssetCooker.RuntimeVariant))
            .ToArray();
        if (staleTiles.Length > 0)
        {
            assetDatabase.RemoveCookedArtifacts(staleTiles);
        }
    }

    internal static CookedTerrainRoot BuildRoot(
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        IReadOnlyList<CookedTerrainTileArtifact> tileArtifacts)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(layerSet);
        ArgumentNullException.ThrowIfNull(tileArtifacts);
        if (layerSet.Guid != root.LayerSet.Guid ||
            !string.Equals(layerSet.PackageId, root.LayerSet.PackageId, StringComparison.Ordinal) ||
            tileArtifacts.Count != root.GeneratedTiles.Count)
        {
            throw new InvalidOperationException(
                "[TerrainRootAssetCooker] Layer-set or tile artifacts do not match the terrain root.");
        }

        CookedTerrainLayer[] layers = layerSet.Layers
            .Select(layer => new CookedTerrainLayer(
                layer.Id,
                ToCookedReference(layer.Albedo),
                ToCookedReference(layer.Normal),
                ToCookedReference(layer.Orm),
                layer.Tint,
                layer.RoughnessMultiplier,
                layer.MetallicMultiplier,
                layer.NormalStrength,
                layer.WorldTiling))
            .ToArray();
        var artifactsByGuid = tileArtifacts.ToDictionary(artifact => artifact.TileGuid);
        var guidsByCoordinate = root.GeneratedTiles.ToDictionary(
            record => record.Coordinate,
            record => record.Guid);
        var tiles = new CookedTerrainTileReference[root.GeneratedTiles.Count];
        for (int index = 0; index < root.GeneratedTiles.Count; index++)
        {
            TerrainGeneratedTileRecord sourceTile = root.GeneratedTiles[index];
            if (!artifactsByGuid.TryGetValue(sourceTile.Guid, out CookedTerrainTileArtifact? artifact) ||
                artifact.RootGuid != root.Guid ||
                artifact.Coordinate != sourceTile.Coordinate ||
                artifact.ContentHash.Length != TerrainCookedContainer.HashSize)
            {
                throw new InvalidOperationException(
                    $"[TerrainRootAssetCooker] Tile artifact {sourceTile.Coordinate} is missing or inconsistent.");
            }

            tiles[index] = new CookedTerrainTileReference(
                sourceTile.Coordinate,
                sourceTile.Guid,
                new TerrainTileNeighborSet(
                    GetNeighborGuid(guidsByCoordinate, sourceTile.Coordinate, -1, 0),
                    GetNeighborGuid(guidsByCoordinate, sourceTile.Coordinate, 1, 0),
                    GetNeighborGuid(guidsByCoordinate, sourceTile.Coordinate, 0, -1),
                    GetNeighborGuid(guidsByCoordinate, sourceTile.Coordinate, 0, 1)),
                artifact.MinHeight,
                artifact.MaxHeight,
                artifact.SizeInBytes,
                artifact.ContentHash.ToArray());
        }

        return new CookedTerrainRoot(
            root.Guid,
            root.PackageId,
            root.SourceSchemaVersion,
            root.Name,
            root.WorldPlacement,
            root.SampleSpacing,
            root.HeightRange,
            root.HeightSource.Width,
            root.HeightSource.Height,
            root.TileResolution,
            root.BorderPolicy,
            root.TileOrigin,
            layerSet.Guid,
            layerSet.PackageId,
            layers,
            tiles);
    }

    internal static byte[] WritePayload(CookedTerrainRoot root)
    {
        ValidateRootForWrite(root);
        string[] strings = BuildStrings(root);
        var stringIndices = new Dictionary<string, uint>(strings.Length, StringComparer.Ordinal);
        for (int index = 0; index < strings.Length; index++)
        {
            stringIndices.Add(strings[index], checked((uint)index));
        }

        byte[] metadata = BuildMetadataSection(root, stringIndices);
        byte[] layers = BuildLayersSection(root.Layers, stringIndices);
        byte[] tiles = BuildTilesSection(root.Tiles);
        TerrainCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedTerrainRootSectionType.Metadata,
                TerrainCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedTerrainRootSectionType.Strings,
                TerrainCookedSectionFlags.Required,
                checked((uint)strings.Length),
                0,
                TerrainCookedContainer.BuildStringSection(strings)),
            new(
                (uint)CookedTerrainRootSectionType.Layers,
                TerrainCookedSectionFlags.Required,
                checked((uint)root.Layers.Count),
                LayerStride,
                layers),
            new(
                (uint)CookedTerrainRootSectionType.Tiles,
                TerrainCookedSectionFlags.Required,
                checked((uint)root.Tiles.Count),
                TileStride,
                tiles)
        ];
        byte[] output = TerrainCookedContainer.Build(
            HeaderSize,
            MaxCookedRootBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], TerrainCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], root.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        TerrainCookedContainer.WriteGuid(header[24..40], root.Guid);
        TerrainCookedContainer.WriteGuid(header[40..56], root.LayerSetGuid);
        BinaryPrimitives.WriteInt32LittleEndian(header[56..], sections.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[60..], root.Tiles.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[64..], root.Layers.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], checked((ulong)output.Length));
        TerrainCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static TerrainCookedAssetDependency[] BuildDependencies(CookedTerrainRoot root)
    {
        var dependencies = new Dictionary<DependencyKey, TerrainCookedAssetDependency>();
        foreach (CookedTerrainTileReference tile in root.Tiles)
        {
            AddDependency(
                dependencies,
                new TerrainCookedAssetDependency(
                    tile.Guid,
                    root.PackageId,
                    TerrainAssetTypes.Tile,
                    TerrainTileAssetCooker.RuntimeVariant,
                    Required: true));
        }

        foreach (CookedTerrainLayer layer in root.Layers)
        {
            AddTextureDependency(
                dependencies,
                layer.Albedo,
                TerrainTextureCookVariants.Albedo);
            AddTextureDependency(
                dependencies,
                layer.Normal,
                TerrainTextureCookVariants.Normal);
            AddTextureDependency(
                dependencies,
                layer.Orm,
                TerrainTextureCookVariants.Orm);
        }

        return dependencies.Values
            .OrderBy(dependency => dependency.Guid)
            .ThenBy(dependency => dependency.PackageId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.AssetType, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Variant, StringComparer.Ordinal)
            .ToArray();
    }

    private static CookedTerrainRoot ReadPayload(
        Guid expectedRootGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"terrain root '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw TerrainCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw TerrainCookedContainer.Invalid(context, "magic is not ARITROOT");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int sourceVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid rootGuid = TerrainCookedContainer.ReadGuid(bytes[24..40]);
        Guid layerSetGuid = TerrainCookedContainer.ReadGuid(bytes[40..56]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]);
        int tileCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        int layerCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[64..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[72..]);
        if (endian != TerrainCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            sourceVersion is < TerrainRootSourceAssetLoader.MinimumSourceSchemaVersion or
                > TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion ||
            headerSize != HeaderSize ||
            rootGuid == Guid.Empty || rootGuid != expectedRootGuid ||
            layerSetGuid == Guid.Empty ||
            tileCount is <= 0 or > TerrainTileIdentity.MaxTileCount ||
            layerCount is < 1 or > TerrainLayerSetSourceAssetLoader.MaxLayerCount ||
            reserved != 0 ||
            declaredSize != checked((ulong)bytes.Length))
        {
            throw TerrainCookedContainer.Invalid(context, "header identity, version, counts, or size is invalid");
        }

        TerrainCookedContainer.EnsureZero(bytes[112..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, TerrainCookedSectionDescriptor> sections =
            TerrainCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedRootBytes,
                s_KnownSections,
                context);
        TerrainCookedSectionDescriptor metadataSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainRootSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        TerrainCookedSectionDescriptor stringsSection = TerrainCookedContainer.RequireVariableSection(
            sections,
            (uint)CookedTerrainRootSectionType.Strings,
            64,
            context);
        TerrainCookedSectionDescriptor layersSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainRootSectionType.Layers,
            LayerStride,
            checked((uint)layerCount),
            TerrainLayerSetSourceAssetLoader.MaxLayerCount,
            context);
        TerrainCookedSectionDescriptor tilesSection = TerrainCookedContainer.RequireSection(
            sections,
            (uint)CookedTerrainRootSectionType.Tiles,
            TileStride,
            checked((uint)tileCount),
            TerrainTileIdentity.MaxTileCount,
            context);
        string[] strings = TerrainCookedContainer.ReadStrings(
            bytes,
            stringsSection,
            MaxStringBytes,
            context);

        ReadOnlySpan<byte> metadata = TerrainCookedContainer.GetSection(bytes, metadataSection);
        string name = TerrainCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata),
            "terrain name",
            context);
        string packageId = TerrainCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[4..]),
            "terrain package id",
            context);
        string layerSetPackageId = TerrainCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[8..]),
            "layer-set package id",
            context);
        uint rawBorderPolicy = BinaryPrimitives.ReadUInt32LittleEndian(metadata[12..]);
        var placement = new WorldPosition(
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[16..]),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[24..]),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[32..]));
        var spacing = new TerrainSampleSpacing(
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[40..]),
            BinaryPrimitives.ReadDoubleLittleEndian(metadata[48..]));
        double heightOffset = BinaryPrimitives.ReadDoubleLittleEndian(metadata[56..]);
        double heightScale = BinaryPrimitives.ReadDoubleLittleEndian(metadata[64..]);
        var heightRange = new TerrainHeightRange(heightOffset, heightOffset + heightScale);
        int resolution = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata[72..]));
        int sourceWidth = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata[76..]));
        int sourceHeight = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata[80..]));
        var tileOrigin = new TerrainTileCoordinate(
            BinaryPrimitives.ReadInt32LittleEndian(metadata[84..]),
            BinaryPrimitives.ReadInt32LittleEndian(metadata[88..]));
        uint metadataReserved = BinaryPrimitives.ReadUInt32LittleEndian(metadata[92..]);
        if (!string.Equals(packageId, expectedPackageId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(layerSetPackageId) ||
            rawBorderPolicy != (uint)TerrainBorderPolicy.SharedEdgeSamples ||
            !placement.IsFinite ||
            !spacing.IsValid ||
            !heightRange.IsValid ||
            metadataReserved != 0)
        {
            throw TerrainCookedContainer.Invalid(context, "metadata identity or numeric fields are invalid");
        }

        ValidateGrid(
            sourceWidth,
            sourceHeight,
            resolution,
            tileOrigin,
            tileCount,
            context,
            out int tileCountX,
            out int tileCountZ);
        CookedTerrainLayer[] layers = ReadLayers(
            bytes,
            layersSection,
            strings,
            layerCount,
            context);
        CookedTerrainTileReference[] tiles = ReadTiles(
            bytes,
            tilesSection,
            rootGuid,
            packageId,
            heightRange,
            tileOrigin,
            tileCountX,
            tileCountZ,
            context);
        return new CookedTerrainRoot(
            rootGuid,
            packageId,
            sourceVersion,
            name,
            placement,
            spacing,
            heightRange,
            sourceWidth,
            sourceHeight,
            resolution,
            TerrainBorderPolicy.SharedEdgeSamples,
            tileOrigin,
            layerSetGuid,
            layerSetPackageId,
            layers,
            tiles);
    }

    private static CookedTerrainLayer[] ReadLayers(
        ReadOnlySpan<byte> bytes,
        TerrainCookedSectionDescriptor descriptor,
        IReadOnlyList<string> strings,
        int layerCount,
        string context)
    {
        ReadOnlySpan<byte> section = TerrainCookedContainer.GetSection(bytes, descriptor);
        var layers = new CookedTerrainLayer[layerCount];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < layers.Length; index++)
        {
            ReadOnlySpan<byte> record = section.Slice(index * LayerStride, LayerStride);
            string id = TerrainCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record),
                $"layer {index} id",
                context);
            if (!ids.Add(id) || BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0)
            {
                throw TerrainCookedContainer.Invalid(context, $"layer '{index}' has duplicate identity or nonzero reserved data");
            }

            CookedTerrainTextureReference albedo = ReadTextureReference(
                record[8..32], strings, $"layer {index} albedo", context);
            CookedTerrainTextureReference normal = ReadTextureReference(
                record[32..56], strings, $"layer {index} normal", context);
            CookedTerrainTextureReference orm = ReadTextureReference(
                record[56..80], strings, $"layer {index} ORM", context);
            var tint = new TerrainLayerTint(
                BinaryPrimitives.ReadSingleLittleEndian(record[80..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[84..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[88..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[92..]));
            float roughnessMultiplier =
                BinaryPrimitives.ReadSingleLittleEndian(record[96..]);
            float metallicMultiplier =
                BinaryPrimitives.ReadSingleLittleEndian(record[100..]);
            float normalStrength =
                BinaryPrimitives.ReadSingleLittleEndian(record[104..]);
            var worldTiling = new TerrainLayerWorldTiling(
                BinaryPrimitives.ReadSingleLittleEndian(record[108..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[112..]));
            TerrainCookedContainer.EnsureZero(
                record[116..],
                context,
                $"layer {index} reserved data");
            if (!tint.IsValid ||
                !worldTiling.IsValid ||
                !TerrainLayerMaterialLimits.IsValid(
                    roughnessMultiplier,
                    metallicMultiplier,
                    normalStrength))
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"layer '{index}' material parameters are invalid");
            }

            layers[index] = new CookedTerrainLayer(
                id,
                albedo,
                normal,
                orm,
                tint,
                roughnessMultiplier,
                metallicMultiplier,
                normalStrength,
                worldTiling);
        }

        return layers;
    }

    private static CookedTerrainTileReference[] ReadTiles(
        ReadOnlySpan<byte> bytes,
        TerrainCookedSectionDescriptor descriptor,
        Guid rootGuid,
        string packageId,
        TerrainHeightRange heightRange,
        TerrainTileCoordinate tileOrigin,
        int tileCountX,
        int tileCountZ,
        string context)
    {
        ReadOnlySpan<byte> section = TerrainCookedContainer.GetSection(bytes, descriptor);
        int tileCount = checked(tileCountX * tileCountZ);
        var tiles = new CookedTerrainTileReference[tileCount];
        var guidsByCoordinate = new Dictionary<TerrainTileCoordinate, Guid>(tileCount);
        for (int index = 0; index < tileCount; index++)
        {
            ReadOnlySpan<byte> record = section.Slice(index * TileStride, TileStride);
            var expectedCoordinate = new TerrainTileCoordinate(
                checked(tileOrigin.X + (index % tileCountX)),
                checked(tileOrigin.Z + (index / tileCountX)));
            var coordinate = new TerrainTileCoordinate(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]));
            Guid tileGuid = TerrainCookedContainer.ReadGuid(record[8..24]);
            Guid expectedGuid = TerrainTileIdentity.CreateGuid(rootGuid, packageId, expectedCoordinate);
            if (coordinate != expectedCoordinate || tileGuid != expectedGuid)
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"tile record '{index}' has stale coordinate or deterministic identity");
            }

            var neighbors = new TerrainTileNeighborSet(
                TerrainCookedContainer.ReadGuid(record[24..40]),
                TerrainCookedContainer.ReadGuid(record[40..56]),
                TerrainCookedContainer.ReadGuid(record[56..72]),
                TerrainCookedContainer.ReadGuid(record[72..88]));
            double minimumHeight = BinaryPrimitives.ReadDoubleLittleEndian(record[88..]);
            double maximumHeight = BinaryPrimitives.ReadDoubleLittleEndian(record[96..]);
            ulong payloadBytes = BinaryPrimitives.ReadUInt64LittleEndian(record[104..]);
            byte[] hash = record[112..144].ToArray();
            if (!double.IsFinite(minimumHeight) ||
                !double.IsFinite(maximumHeight) ||
                minimumHeight > maximumHeight ||
                minimumHeight < heightRange.Min ||
                maximumHeight > heightRange.Max ||
                payloadBytes < TerrainTileAssetCooker.HeaderSize ||
                payloadBytes > TerrainTileAssetCooker.MaxCookedTileBytes ||
                hash.All(value => value == 0))
            {
                throw TerrainCookedContainer.Invalid(context, $"tile record '{index}' has invalid bounds, size, or hash");
            }

            tiles[index] = new CookedTerrainTileReference(
                coordinate,
                tileGuid,
                neighbors,
                minimumHeight,
                maximumHeight,
                checked((long)payloadBytes),
                hash);
            guidsByCoordinate.Add(coordinate, tileGuid);
        }

        foreach (CookedTerrainTileReference tile in tiles)
        {
            TerrainTileNeighborSet expected = new(
                GetNeighborGuid(guidsByCoordinate, tile.Coordinate, -1, 0),
                GetNeighborGuid(guidsByCoordinate, tile.Coordinate, 1, 0),
                GetNeighborGuid(guidsByCoordinate, tile.Coordinate, 0, -1),
                GetNeighborGuid(guidsByCoordinate, tile.Coordinate, 0, 1));
            if (tile.Neighbors != expected)
            {
                throw TerrainCookedContainer.Invalid(
                    context,
                    $"tile {tile.Coordinate} has invalid neighbor identities");
            }
        }

        return tiles;
    }

    private static byte[] BuildMetadataSection(
        CookedTerrainRoot root,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[MetadataStride];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), stringIndices[root.Name]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), stringIndices[root.PackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), stringIndices[root.LayerSetPackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)root.BorderPolicy);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(16), root.WorldPlacement.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(24), root.WorldPlacement.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(32), root.WorldPlacement.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(40), root.SampleSpacing.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(48), root.SampleSpacing.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(56), root.HeightRange.Min);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(64), root.HeightRange.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), checked((uint)root.TileResolution));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), checked((uint)root.HeightSourceWidth));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), checked((uint)root.HeightSourceHeight));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(84), root.TileOrigin.X);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(88), root.TileOrigin.Z);
        return bytes;
    }

    private static byte[] BuildLayersSection(
        IReadOnlyList<CookedTerrainLayer> layers,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(layers.Count * LayerStride)];
        for (int index = 0; index < layers.Count; index++)
        {
            Span<byte> record = bytes.AsSpan(index * LayerStride, LayerStride);
            CookedTerrainLayer layer = layers[index];
            BinaryPrimitives.WriteUInt32LittleEndian(record, stringIndices[layer.Id]);
            WriteTextureReference(record[8..32], layer.Albedo, stringIndices);
            WriteTextureReference(record[32..56], layer.Normal, stringIndices);
            WriteTextureReference(record[56..80], layer.Orm, stringIndices);
            BinaryPrimitives.WriteSingleLittleEndian(record[80..], layer.Tint.R);
            BinaryPrimitives.WriteSingleLittleEndian(record[84..], layer.Tint.G);
            BinaryPrimitives.WriteSingleLittleEndian(record[88..], layer.Tint.B);
            BinaryPrimitives.WriteSingleLittleEndian(record[92..], layer.Tint.A);
            BinaryPrimitives.WriteSingleLittleEndian(
                record[96..],
                layer.RoughnessMultiplier);
            BinaryPrimitives.WriteSingleLittleEndian(
                record[100..],
                layer.MetallicMultiplier);
            BinaryPrimitives.WriteSingleLittleEndian(
                record[104..],
                layer.NormalStrength);
            BinaryPrimitives.WriteSingleLittleEndian(record[108..], layer.WorldTiling.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[112..], layer.WorldTiling.Z);
        }

        return bytes;
    }

    private static byte[] BuildTilesSection(IReadOnlyList<CookedTerrainTileReference> tiles)
    {
        byte[] bytes = new byte[checked(tiles.Count * TileStride)];
        for (int index = 0; index < tiles.Count; index++)
        {
            Span<byte> record = bytes.AsSpan(index * TileStride, TileStride);
            CookedTerrainTileReference tile = tiles[index];
            BinaryPrimitives.WriteInt32LittleEndian(record, tile.Coordinate.X);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], tile.Coordinate.Z);
            TerrainCookedContainer.WriteGuid(record[8..24], tile.Guid);
            TerrainCookedContainer.WriteGuid(record[24..40], tile.Neighbors.NegativeX);
            TerrainCookedContainer.WriteGuid(record[40..56], tile.Neighbors.PositiveX);
            TerrainCookedContainer.WriteGuid(record[56..72], tile.Neighbors.NegativeZ);
            TerrainCookedContainer.WriteGuid(record[72..88], tile.Neighbors.PositiveZ);
            BinaryPrimitives.WriteDoubleLittleEndian(record[88..], tile.MinHeight);
            BinaryPrimitives.WriteDoubleLittleEndian(record[96..], tile.MaxHeight);
            BinaryPrimitives.WriteUInt64LittleEndian(record[104..], checked((ulong)tile.PayloadBytes));
            tile.ContentHash.CopyTo(record[112..144]);
        }

        return bytes;
    }

    private static string[] BuildStrings(CookedTerrainRoot root)
    {
        return root.Layers
            .SelectMany(layer => new[]
            {
                layer.Id,
                layer.Albedo.PackageId,
                layer.Normal.PackageId,
                layer.Orm.PackageId
            })
            .Append(root.Name)
            .Append(root.PackageId)
            .Append(root.LayerSetPackageId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRootForWrite(CookedTerrainRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string context = $"terrain root '{root.Guid:D}'";
        if (root.Guid == Guid.Empty ||
            root.LayerSetGuid == Guid.Empty ||
            root.SourceSchemaVersion is < TerrainRootSourceAssetLoader.MinimumSourceSchemaVersion or
                > TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion ||
            !string.Equals(root.PackageId, NormalizePackageId(root.PackageId), StringComparison.Ordinal) ||
            !string.Equals(root.LayerSetPackageId, NormalizePackageId(root.LayerSetPackageId), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(root.Name) ||
            !root.WorldPlacement.IsFinite ||
            !root.SampleSpacing.IsValid ||
            !root.HeightRange.IsValid ||
            root.BorderPolicy != TerrainBorderPolicy.SharedEdgeSamples ||
            root.Layers.Count is < 1 or > TerrainLayerSetSourceAssetLoader.MaxLayerCount ||
            root.Tiles.Count is <= 0 or > TerrainTileIdentity.MaxTileCount)
        {
            throw new InvalidOperationException(
                $"[TerrainRootAssetCooker] {context} has invalid identity or metadata.");
        }

        ValidateGrid(
            root.HeightSourceWidth,
            root.HeightSourceHeight,
            root.TileResolution,
            root.TileOrigin,
            root.Tiles.Count,
            context,
            out int tileCountX,
            out _);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CookedTerrainLayer layer in root.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || !ids.Add(layer.Id))
            {
                throw new InvalidOperationException(
                    $"[TerrainRootAssetCooker] {context} contains an empty or duplicate layer id.");
            }

            ValidateTextureReference(layer.Albedo, context);
            ValidateTextureReference(layer.Normal, context);
            ValidateTextureReference(layer.Orm, context);
            if (!layer.Tint.IsValid ||
                !layer.WorldTiling.IsValid ||
                !TerrainLayerMaterialLimits.IsValid(
                    layer.RoughnessMultiplier,
                    layer.MetallicMultiplier,
                    layer.NormalStrength))
            {
                throw new InvalidOperationException(
                    $"[TerrainRootAssetCooker] {context} layer '{layer.Id}' has invalid material parameters.");
            }
        }

        var guidsByCoordinate = root.Tiles.ToDictionary(tile => tile.Coordinate, tile => tile.Guid);
        for (int index = 0; index < root.Tiles.Count; index++)
        {
            CookedTerrainTileReference tile = root.Tiles[index];
            var expectedCoordinate = new TerrainTileCoordinate(
                checked(root.TileOrigin.X + (index % tileCountX)),
                checked(root.TileOrigin.Z + (index / tileCountX)));
            Guid expectedGuid = TerrainTileIdentity.CreateGuid(root.Guid, root.PackageId, expectedCoordinate);
            TerrainTileNeighborSet expectedNeighbors = new(
                GetNeighborGuid(guidsByCoordinate, expectedCoordinate, -1, 0),
                GetNeighborGuid(guidsByCoordinate, expectedCoordinate, 1, 0),
                GetNeighborGuid(guidsByCoordinate, expectedCoordinate, 0, -1),
                GetNeighborGuid(guidsByCoordinate, expectedCoordinate, 0, 1));
            if (tile.Coordinate != expectedCoordinate ||
                tile.Guid != expectedGuid ||
                tile.Neighbors != expectedNeighbors ||
                !double.IsFinite(tile.MinHeight) ||
                !double.IsFinite(tile.MaxHeight) ||
                tile.MinHeight > tile.MaxHeight ||
                tile.MinHeight < root.HeightRange.Min ||
                tile.MaxHeight > root.HeightRange.Max ||
                tile.PayloadBytes < TerrainTileAssetCooker.HeaderSize ||
                tile.PayloadBytes > TerrainTileAssetCooker.MaxCookedTileBytes ||
                tile.ContentHash.Length != TerrainCookedContainer.HashSize ||
                tile.ContentHash.All(value => value == 0))
            {
                throw new InvalidOperationException(
                    $"[TerrainRootAssetCooker] {context} tile '{index}' is noncanonical or invalid.");
            }
        }
    }

    private static void ValidateGrid(
        int sourceWidth,
        int sourceHeight,
        int resolution,
        TerrainTileCoordinate tileOrigin,
        int declaredTileCount,
        string context,
        out int tileCountX,
        out int tileCountZ)
    {
        int intervals = resolution - 1;
        if (resolution < TerrainRootSourceAssetLoader.MinTileResolution ||
            resolution > TerrainRootSourceAssetLoader.MaxTileResolution ||
            (intervals & (intervals - 1)) != 0 ||
            sourceWidth < resolution ||
            sourceHeight < resolution ||
            sourceWidth > TerrainHeightSourceDecoder.MaxDimension ||
            sourceHeight > TerrainHeightSourceDecoder.MaxDimension ||
            (sourceWidth - 1) % intervals != 0 ||
            (sourceHeight - 1) % intervals != 0)
        {
            throw TerrainCookedContainer.Invalid(context, "source dimensions or tile resolution are invalid");
        }

        TerrainTileIdentity.ValidateCoordinate(tileOrigin);
        tileCountX = (sourceWidth - 1) / intervals;
        tileCountZ = (sourceHeight - 1) / intervals;
        int expectedCount = checked(tileCountX * tileCountZ);
        if (expectedCount != declaredTileCount || expectedCount > TerrainTileIdentity.MaxTileCount)
        {
            throw TerrainCookedContainer.Invalid(
                context,
                $"tile count '{declaredTileCount}' does not match source grid '{expectedCount}'");
        }

        TerrainTileIdentity.ValidateCoordinate(new TerrainTileCoordinate(
            checked(tileOrigin.X + tileCountX - 1),
            checked(tileOrigin.Z + tileCountZ - 1)));
    }

    private static void WriteTextureReference(
        Span<byte> bytes,
        CookedTerrainTextureReference reference,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        TerrainCookedContainer.WriteGuid(bytes[..16], reference.Guid);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], stringIndices[reference.PackageId]);
    }

    private static CookedTerrainTextureReference ReadTextureReference(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<string> strings,
        string field,
        string context)
    {
        Guid guid = TerrainCookedContainer.ReadGuid(bytes[..16]);
        string packageId = TerrainCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            field + " package id",
            context);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        var reference = new CookedTerrainTextureReference(guid, packageId);
        if (reserved != 0)
        {
            throw TerrainCookedContainer.Invalid(context, $"{field} has nonzero reserved data");
        }

        ValidateTextureReference(reference, context);
        return reference;
    }

    private static void ValidateTextureReference(
        CookedTerrainTextureReference reference,
        string context)
    {
        if (reference.Guid == Guid.Empty ||
            !string.Equals(reference.PackageId, NormalizePackageId(reference.PackageId), StringComparison.Ordinal))
        {
            throw TerrainCookedContainer.Invalid(context, "texture dependency identity is invalid");
        }
    }

    private static CookedTerrainTextureReference ToCookedReference(
        AssetRef<Texture2DSourceAsset> reference)
    {
        return new CookedTerrainTextureReference(
            reference.Guid,
            NormalizePackageId(reference.PackageId));
    }

    private static Guid GetNeighborGuid(
        IReadOnlyDictionary<TerrainTileCoordinate, Guid> guidsByCoordinate,
        TerrainTileCoordinate coordinate,
        int offsetX,
        int offsetZ)
    {
        var neighbor = new TerrainTileCoordinate(
            checked(coordinate.X + offsetX),
            checked(coordinate.Z + offsetZ));
        return guidsByCoordinate.TryGetValue(neighbor, out Guid guid) ? guid : Guid.Empty;
    }

    private static void AddTextureDependency(
        IDictionary<DependencyKey, TerrainCookedAssetDependency> dependencies,
        CookedTerrainTextureReference texture,
        string variant)
    {
        AddDependency(
            dependencies,
            new TerrainCookedAssetDependency(
                texture.Guid,
                texture.PackageId,
                TextureAssetType,
                variant,
                Required: true));
    }

    private static void AddDependency(
        IDictionary<DependencyKey, TerrainCookedAssetDependency> dependencies,
        TerrainCookedAssetDependency dependency)
    {
        var key = new DependencyKey(
            dependency.Guid,
            dependency.PackageId,
            dependency.AssetType,
            dependency.Variant);
        dependencies.TryAdd(key, dependency);
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

    private readonly record struct DependencyKey(
        Guid Guid,
        string PackageId,
        string AssetType,
        string Variant);
}
