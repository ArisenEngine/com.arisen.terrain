using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Serialization;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain.Assets;

[Flags]
public enum TerrainImportDestructiveChange
{
    None = 0,
    RootIdentity = 1 << 0,
    TileGrid = 1 << 1,
    WorldLayout = 1 << 2
}

public sealed record TerrainImportRequest(
    string HeightSourcePath,
    string PackageAssetsRoot,
    string PackageId,
    string OutputDirectory,
    string AssetName,
    string DisplayName,
    Guid NewRootGuid,
    WorldBounds WorldBounds,
    int TileResolution,
    TerrainTileCoordinate TileOrigin,
    AssetRef<TerrainLayerSetSourceAsset> LayerSet,
    bool RegenerateRootIdentity = false);

public sealed record TerrainTileImportPreview(
    TerrainTileCoordinate Coordinate,
    Guid Guid,
    WorldBounds Bounds,
    WorldCellCoordinate? OwnerCell,
    IReadOnlyList<WorldCellCoordinate> IntersectingCells,
    string GeneratedAssetPath);

public sealed class TerrainImportPlan
{
    internal TerrainImportPlan(
        TerrainImportRequest request,
        AssetRecord layerSetAsset,
        WorldPartitionSettings? worldPartition,
        Guid rootGuid,
        string rootAssetPath,
        string rootMetadataPath,
        string heightAssetPath,
        string weightAssetPath,
        int sourceWidth,
        int sourceHeight,
        TerrainSampleSpacing sampleSpacing,
        TerrainHeightRange heightRange,
        IReadOnlyList<TerrainTileImportPreview> tiles,
        TerrainImportDestructiveChange destructiveChanges,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<TerrainImportFileWrite> writes,
        IReadOnlyList<string> obsoletePaths,
        IReadOnlyList<Guid> obsoleteTileGuids,
        IReadOnlyList<Guid> previousTileGuids,
        string fingerprint,
        bool replacesExistingRoot,
        Guid? previousRootGuid)
    {
        Request = request;
        LayerSetAsset = layerSetAsset;
        WorldPartition = worldPartition;
        RootGuid = rootGuid;
        RootAssetPath = rootAssetPath;
        RootMetadataPath = rootMetadataPath;
        HeightAssetPath = heightAssetPath;
        WeightAssetPath = weightAssetPath;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        SampleSpacing = sampleSpacing;
        HeightRange = heightRange;
        Tiles = tiles;
        DestructiveChanges = destructiveChanges;
        Diagnostics = diagnostics;
        Writes = writes;
        ObsoletePaths = obsoletePaths;
        ObsoleteTileGuids = obsoleteTileGuids;
        PreviousTileGuids = previousTileGuids;
        Fingerprint = fingerprint;
        ReplacesExistingRoot = replacesExistingRoot;
        PreviousRootGuid = previousRootGuid;
    }

    public TerrainImportRequest Request { get; }
    public Guid RootGuid { get; }
    public string RootAssetPath { get; }
    public string RootMetadataPath { get; }
    public string HeightAssetPath { get; }
    public string WeightAssetPath { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public TerrainSampleSpacing SampleSpacing { get; }
    public TerrainHeightRange HeightRange { get; }
    public IReadOnlyList<TerrainTileImportPreview> Tiles { get; }
    public TerrainImportDestructiveChange DestructiveChanges { get; }
    public bool RequiresRegenerationConfirmation =>
        DestructiveChanges != TerrainImportDestructiveChange.None;
    public IReadOnlyList<string> Diagnostics { get; }
    public bool ReplacesExistingRoot { get; }
    public Guid? PreviousRootGuid { get; }
    public IReadOnlyList<Guid> PreviousTileGuids { get; }

    internal AssetRecord LayerSetAsset { get; }
    internal WorldPartitionSettings? WorldPartition { get; }
    internal IReadOnlyList<TerrainImportFileWrite> Writes { get; }
    internal IReadOnlyList<string> ObsoletePaths { get; }
    internal IReadOnlyList<Guid> ObsoleteTileGuids { get; }
    internal string Fingerprint { get; }
}

public readonly record struct TerrainImportCommitOptions(
    bool ConfirmDestructiveRegeneration = false);

public sealed record TerrainImportCommitResult(
    Guid RootGuid,
    Guid? ReplacedRootGuid,
    string RootAssetPath,
    IReadOnlyList<Guid> TileGuids,
    IReadOnlyList<Guid> RemovedTileGuids,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> RemovedPaths,
    bool ReplacedExistingRoot);

internal sealed record TerrainImportFileWrite(string Path, byte[] Bytes);

public static class TerrainImportPlanner
{
    public const string RootImporter = "ArisenTerrainRootImporter";
    public const string HeightImporter = "Pgm16TerrainHeightImporter";
    public const string HeightChildKind = "terrain-height-source";
    public const string WeightImporter = "ArisenTerrainWeightSourceImporter";
    public const string WeightChildKind = "terrain-weight-source";
    public const int MaxCellIntersectionsPerTile = 4_096;

    public static TerrainImportPlan CreatePlan(
        TerrainImportRequest request,
        AssetRecord layerSetAsset,
        WorldPartitionSettings? worldPartition = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(layerSetAsset);

        string assetsRoot = ValidateAssetsRoot(request.PackageAssetsRoot);
        string packageId = NormalizePackageId(request.PackageId);
        string assetName = ValidateAssetName(request.AssetName);
        string displayName = NormalizeDisplayName(request.DisplayName);
        string outputDirectory = ResolveOutputDirectory(
            assetsRoot,
            request.OutputDirectory);
        string rootAssetPath = Path.Combine(outputDirectory, assetName + ".aristerrain");
        string rootMetadataPath = rootAssetPath + ".meta";
        string heightAssetPath = Path.Combine(outputDirectory, "Height", assetName + ".pgm");
        string weightAssetPath = Path.Combine(outputDirectory, assetName + ".ariweights");
        string generatedDirectory = Path.Combine(outputDirectory, "Generated", assetName);
        string sourcePath = ValidateHeightSource(request.HeightSourcePath);

        ValidateLayerSet(request.LayerSet, layerSetAsset);
        _ = TerrainLayerSetSourceAssetLoader.LoadSource(layerSetAsset);
        ValidateWorldBounds(request.WorldBounds);
        ValidateWorldPartition(worldPartition);

        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(sourcePath);
        ValidateTileLayout(heightField.Width, heightField.Height, request.TileResolution);
        TerrainTileIdentity.ValidateCoordinate(request.TileOrigin);

        ExistingTerrainRoot? existing = ReadExistingRoot(
            rootAssetPath,
            rootMetadataPath,
            packageId);
        Guid rootGuid = ResolveRootGuid(request, existing);
        int tileIntervals = request.TileResolution - 1;
        int tileCountX = (heightField.Width - 1) / tileIntervals;
        int tileCountZ = (heightField.Height - 1) / tileIntervals;
        TerrainGeneratedTileRecord[] generatedTiles = TerrainTileIdentity.CreateRecords(
            rootGuid,
            packageId,
            request.TileOrigin,
            tileCountX,
            tileCountZ);

        double horizontalSizeX = request.WorldBounds.Max.X - request.WorldBounds.Min.X;
        double horizontalSizeZ = request.WorldBounds.Max.Z - request.WorldBounds.Min.Z;
        var sampleSpacing = new TerrainSampleSpacing(
            horizontalSizeX / (heightField.Width - 1),
            horizontalSizeZ / (heightField.Height - 1));
        var heightRange = new TerrainHeightRange(
            0.0,
            request.WorldBounds.Max.Y - request.WorldBounds.Min.Y);
        if (!sampleSpacing.IsValid || !heightRange.IsValid)
        {
            throw new InvalidOperationException(
                "[TerrainImportPlanner] Derived sample spacing or height range is invalid.");
        }

        var normalizedRequest = request with
        {
            HeightSourcePath = sourcePath,
            PackageAssetsRoot = assetsRoot,
            PackageId = packageId,
            OutputDirectory = Path.GetRelativePath(assetsRoot, outputDirectory).Replace('\\', '/'),
            AssetName = assetName,
            DisplayName = displayName
        };
        TerrainTileImportPreview[] tiles = BuildTilePreviews(
            normalizedRequest,
            rootGuid,
            generatedTiles,
            sampleSpacing,
            heightRange,
            generatedDirectory,
            worldPartition);
        TerrainImportDestructiveChange destructiveChanges = ClassifyChanges(
            existing,
            rootGuid,
            normalizedRequest,
            heightField.Width,
            heightField.Height,
            generatedTiles);

        string authoredHeightPath = Path.GetRelativePath(
                Path.GetDirectoryName(rootAssetPath)!,
                heightAssetPath)
            .Replace('\\', '/');
        string authoredWeightPath = Path.GetRelativePath(
                Path.GetDirectoryName(rootAssetPath)!,
                weightAssetPath)
            .Replace('\\', '/');
        string rootSource = BuildRootSource(
            rootGuid,
            displayName,
            normalizedRequest.WorldBounds.Min,
            sampleSpacing,
            heightRange,
            authoredHeightPath,
            authoredWeightPath,
            normalizedRequest.TileResolution,
            normalizedRequest.TileOrigin,
            normalizedRequest.LayerSet,
            generatedTiles);

        var writes = new List<TerrainImportFileWrite>(4 + (tiles.Length * 2));
        AddUtf8Write(writes, rootAssetPath, rootSource);
        AddUtf8Write(
            writes,
            rootMetadataPath,
            BuildMetadata(rootGuid, TerrainAssetTypes.Root, RootImporter, null));

        bool sourceIsDestination = PathsEqual(sourcePath, heightAssetPath);
        ValidateGeneratedSourceOutputOwnership(
            heightAssetPath,
            existing?.Guid,
            rootGuid,
            HeightChildKind,
            "height",
            allowUntrackedSource: sourceIsDestination && existing == null,
            allowLegacyOwnedSource: existing != null &&
                PathsEqual(existing.Descriptor.HeightSource.ResolvedPath, heightAssetPath),
            expectedAssetType: "TerrainHeightSource",
            expectedImporter: HeightImporter);
        writes.Add(new TerrainImportFileWrite(heightAssetPath, File.ReadAllBytes(sourcePath)));
        AssetMetadata heightMetadata = GeneratedAssetIdentity.CreateChildMetadata(
            rootGuid,
            packageId,
            HeightChildKind,
            "height",
            "TerrainHeightSource",
            HeightImporter);
        AddUtf8Write(
            writes,
            heightAssetPath + ".meta",
            BuildMetadata(
                heightMetadata.Guid,
                heightMetadata.AssetType,
                heightMetadata.Importer,
                heightMetadata.Generated));

        ValidateGeneratedSourceOutputOwnership(
            weightAssetPath,
            existing?.Guid,
            rootGuid,
            WeightChildKind,
            "weight",
            allowUntrackedSource: false,
            allowLegacyOwnedSource: existing?.Descriptor.WeightSource is { } legacyWeights &&
                PathsEqual(legacyWeights.ResolvedPath, weightAssetPath),
            expectedAssetType: "TerrainWeightSource",
            expectedImporter: WeightImporter);
        byte[] weightBytes;
        if (existing?.Descriptor.WeightSource is { } existingWeights &&
            existingWeights.Width == heightField.Width &&
            existingWeights.Height == heightField.Height &&
            File.Exists(existingWeights.ResolvedPath))
        {
            TerrainWeightField retained = TerrainWeightSourceDecoder.DecodeFile(
                existingWeights.ResolvedPath);
            weightBytes = TerrainWeightSourceEncoder.Encode(
                retained.Width,
                retained.Height,
                retained.Weights.Span);
        }
        else
        {
            var defaultWeights = new byte[
                checked(heightField.Width * heightField.Height * TerrainCookedFormat.WeightChannelCount)];
            for (int sample = 0; sample < heightField.Width * heightField.Height; sample++)
            {
                defaultWeights[sample * TerrainCookedFormat.WeightChannelCount] = byte.MaxValue;
            }
            weightBytes = TerrainWeightSourceEncoder.Encode(
                heightField.Width,
                heightField.Height,
                defaultWeights);
        }
        writes.Add(new TerrainImportFileWrite(
            weightAssetPath,
            weightBytes));
        AssetMetadata weightMetadata = GeneratedAssetIdentity.CreateChildMetadata(
            rootGuid,
            packageId,
            WeightChildKind,
            "weights",
            "TerrainWeightSource",
            WeightImporter);
        AddUtf8Write(
            writes,
            weightAssetPath + ".meta",
            BuildMetadata(
                weightMetadata.Guid,
                weightMetadata.AssetType,
                weightMetadata.Importer,
                weightMetadata.Generated));

        GeneratedOutputInspection outputInspection = InspectGeneratedOutputs(
            outputDirectory,
            generatedDirectory,
            existing?.Guid,
            rootGuid,
            tiles);
        foreach (TerrainTileImportPreview tile in tiles)
        {
            AddUtf8Write(
                writes,
                tile.GeneratedAssetPath,
                $"Generated terrain tile identity. Runtime payload is cooked from {assetName}.aristerrain.\n");
            AssetMetadata metadata = TerrainTileIdentity.CreateMetadata(
                rootGuid,
                packageId,
                tile.Coordinate);
            AddUtf8Write(
                writes,
                tile.GeneratedAssetPath + ".meta",
                BuildMetadata(
                    metadata.Guid,
                    metadata.AssetType,
                    metadata.Importer,
                    metadata.Generated));
        }

        var diagnostics = BuildDiagnostics(
            existing,
            worldPartition,
            tiles,
            destructiveChanges,
            outputInspection.ObsoletePaths.Count);
        string fingerprint = BuildFingerprint(
            normalizedRequest,
            layerSetAsset,
            worldPartition,
            rootGuid,
            sourcePath,
            rootAssetPath,
            rootMetadataPath,
            heightAssetPath,
            weightAssetPath,
            Path.Combine(outputDirectory, "Generated"),
            rootSource,
            destructiveChanges);

        return new TerrainImportPlan(
            normalizedRequest,
            layerSetAsset,
            worldPartition,
            rootGuid,
            rootAssetPath,
            rootMetadataPath,
            heightAssetPath,
            weightAssetPath,
            heightField.Width,
            heightField.Height,
            sampleSpacing,
            heightRange,
            tiles,
            destructiveChanges,
            diagnostics,
            writes,
            outputInspection.ObsoletePaths,
            outputInspection.ObsoleteTileGuids,
            existing?.Descriptor.GeneratedTiles.Select(tile => tile.Guid).ToArray() ?? [],
            fingerprint,
            existing != null,
            existing?.Guid);
    }

    private static TerrainTileImportPreview[] BuildTilePreviews(
        TerrainImportRequest request,
        Guid rootGuid,
        IReadOnlyList<TerrainGeneratedTileRecord> generatedTiles,
        TerrainSampleSpacing sampleSpacing,
        TerrainHeightRange heightRange,
        string generatedDirectory,
        WorldPartitionSettings? worldPartition)
    {
        int intervals = request.TileResolution - 1;
        double tileSizeX = intervals * sampleSpacing.X;
        double tileSizeZ = intervals * sampleSpacing.Z;
        var tiles = new TerrainTileImportPreview[generatedTiles.Count];
        for (int index = 0; index < generatedTiles.Count; index++)
        {
            TerrainGeneratedTileRecord record = generatedTiles[index];
            int localX = record.Coordinate.X - request.TileOrigin.X;
            int localZ = record.Coordinate.Z - request.TileOrigin.Z;
            var minimum = new WorldPosition(
                request.WorldBounds.Min.X + (localX * tileSizeX),
                request.WorldBounds.Min.Y + heightRange.Min,
                request.WorldBounds.Min.Z + (localZ * tileSizeZ));
            var maximum = new WorldPosition(
                minimum.X + tileSizeX,
                request.WorldBounds.Min.Y + heightRange.Max,
                minimum.Z + tileSizeZ);
            var bounds = new WorldBounds(minimum, maximum);
            WorldCellCoordinate? owner = null;
            IReadOnlyList<WorldCellCoordinate> intersections = Array.Empty<WorldCellCoordinate>();
            if (worldPartition != null)
            {
                var center = new WorldPosition(
                    minimum.X + ((maximum.X - minimum.X) * 0.5),
                    minimum.Y + ((maximum.Y - minimum.Y) * 0.5),
                    minimum.Z + ((maximum.Z - minimum.Z) * 0.5));
                owner = WorldPartitionCoordinates.GetCoordinate(worldPartition, center);
                intersections = GetIntersectingCells(worldPartition, bounds);
            }

            string coordinateName = string.Create(
                CultureInfo.InvariantCulture,
                $"x_{record.Coordinate.X}_z_{record.Coordinate.Z}.ariterraingenerated");
            tiles[index] = new TerrainTileImportPreview(
                record.Coordinate,
                record.Guid,
                bounds,
                owner,
                intersections,
                Path.Combine(generatedDirectory, coordinateName));
        }

        return tiles;
    }

    private static IReadOnlyList<WorldCellCoordinate> GetIntersectingCells(
        WorldPartitionSettings partition,
        WorldBounds bounds)
    {
        var inclusiveMaximum = new WorldPosition(
            Math.BitDecrement(bounds.Max.X),
            Math.BitDecrement(bounds.Max.Y),
            Math.BitDecrement(bounds.Max.Z));
        WorldCellCoordinate minimum = WorldPartitionCoordinates.GetCoordinate(partition, bounds.Min);
        WorldCellCoordinate maximum = WorldPartitionCoordinates.GetCoordinate(partition, inclusiveMaximum);
        long count = checked(
            ((long)maximum.X - minimum.X + 1) *
            ((long)maximum.Y - minimum.Y + 1) *
            ((long)maximum.Z - minimum.Z + 1));
        if (count <= 0 || count > MaxCellIntersectionsPerTile)
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Tile bounds intersect '{count}' world cells; maximum is {MaxCellIntersectionsPerTile}.");
        }

        var cells = new List<WorldCellCoordinate>((int)count);
        for (long y = minimum.Y; y <= maximum.Y; y++)
        {
            for (long z = minimum.Z; z <= maximum.Z; z++)
            {
                for (long x = minimum.X; x <= maximum.X; x++)
                {
                    cells.Add(new WorldCellCoordinate((int)x, (int)y, (int)z));
                }
            }
        }

        return cells;
    }

    private static ExistingTerrainRoot? ReadExistingRoot(
        string rootAssetPath,
        string rootMetadataPath,
        string packageId)
    {
        bool hasSource = File.Exists(rootAssetPath);
        bool hasMetadata = File.Exists(rootMetadataPath);
        if (hasSource != hasMetadata)
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Existing terrain output must contain both source and metadata: '{rootAssetPath}'.");
        }

        if (!hasSource)
        {
            return null;
        }

        AssetMetadata metadata = ReadMetadata(rootMetadataPath);
        if (metadata.Guid == Guid.Empty ||
            !string.Equals(metadata.AssetType, TerrainAssetTypes.Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Existing output metadata '{rootMetadataPath}' is not a TerrainRoot identity.");
        }

        var record = new AssetRecord(
            metadata.Guid,
            TerrainAssetTypes.Root,
            rootAssetPath,
            rootMetadataPath,
            packageId);
        TerrainRootSourceDescriptor descriptor = TerrainRootSourceAssetLoader.LoadSource(record);
        return new ExistingTerrainRoot(metadata.Guid, descriptor);
    }

    private static Guid ResolveRootGuid(
        TerrainImportRequest request,
        ExistingTerrainRoot? existing)
    {
        if (existing != null && !request.RegenerateRootIdentity)
        {
            return existing.Guid;
        }

        if (request.NewRootGuid == Guid.Empty)
        {
            throw new ArgumentException(
                "[TerrainImportPlanner] New or explicitly regenerated terrain requires a non-empty root GUID.",
                nameof(request));
        }

        return request.NewRootGuid;
    }

    private static TerrainImportDestructiveChange ClassifyChanges(
        ExistingTerrainRoot? existing,
        Guid rootGuid,
        TerrainImportRequest request,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<TerrainGeneratedTileRecord> generatedTiles)
    {
        if (existing == null)
        {
            return TerrainImportDestructiveChange.None;
        }

        TerrainRootSourceDescriptor current = existing.Descriptor;
        TerrainImportDestructiveChange changes = TerrainImportDestructiveChange.None;
        if (existing.Guid != rootGuid)
        {
            changes |= TerrainImportDestructiveChange.RootIdentity;
        }

        if (current.TileResolution != request.TileResolution ||
            current.TileOrigin != request.TileOrigin ||
            current.HeightSource.Width != sourceWidth ||
            current.HeightSource.Height != sourceHeight ||
            current.GeneratedTiles.Count != generatedTiles.Count ||
            !current.GeneratedTiles.Select(tile => tile.Coordinate)
                .SequenceEqual(generatedTiles.Select(tile => tile.Coordinate)))
        {
            changes |= TerrainImportDestructiveChange.TileGrid;
        }

        WorldBounds currentBounds = GetWorldBounds(current);
        if (currentBounds != request.WorldBounds)
        {
            changes |= TerrainImportDestructiveChange.WorldLayout;
        }

        return changes;
    }

    private static WorldBounds GetWorldBounds(TerrainRootSourceDescriptor descriptor)
    {
        return new WorldBounds(
            new WorldPosition(
                descriptor.WorldPlacement.X,
                descriptor.WorldPlacement.Y + descriptor.HeightRange.Min,
                descriptor.WorldPlacement.Z),
            new WorldPosition(
                descriptor.WorldPlacement.X +
                    ((descriptor.HeightSource.Width - 1) * descriptor.SampleSpacing.X),
                descriptor.WorldPlacement.Y + descriptor.HeightRange.Max,
                descriptor.WorldPlacement.Z +
                    ((descriptor.HeightSource.Height - 1) * descriptor.SampleSpacing.Z)));
    }

    private static GeneratedOutputInspection InspectGeneratedOutputs(
        string outputDirectory,
        string generatedDirectory,
        Guid? existingRootGuid,
        Guid desiredRootGuid,
        IReadOnlyList<TerrainTileImportPreview> desiredTiles)
    {
        string generatedSearchRoot = Path.Combine(outputDirectory, "Generated");
        if (!Directory.Exists(generatedSearchRoot))
        {
            return new GeneratedOutputInspection(Array.Empty<string>(), Array.Empty<Guid>());
        }

        var expectedByPath = desiredTiles.ToDictionary(
            tile => NormalizePath(tile.GeneratedAssetPath),
            tile => tile,
            StringComparer.OrdinalIgnoreCase);
        var observedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desiredGuids = new HashSet<Guid>(desiredTiles.Select(tile => tile.Guid));
        var obsoletePaths = new List<string>();
        var obsoleteGuids = new HashSet<Guid>();
        foreach (string metaPath in Directory.EnumerateFiles(
                     generatedSearchRoot,
                     "*.meta",
                     SearchOption.AllDirectories))
        {
            string sourcePath = metaPath[..^".meta".Length];
            bool isCanonicalOutput = IsSameOrChildPath(sourcePath, generatedDirectory);
            if (!string.Equals(
                    Path.GetExtension(sourcePath),
                    ".ariterraingenerated",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (isCanonicalOutput)
                {
                    throw new InvalidOperationException(
                        $"[TerrainImportPlanner] Terrain generated directory contains unexpected metadata '{metaPath}'.");
                }

                continue;
            }

            if (!File.Exists(sourcePath))
            {
                if (isCanonicalOutput)
                {
                    throw new InvalidOperationException(
                        $"[TerrainImportPlanner] Generated terrain metadata has no source file: '{metaPath}'.");
                }

                continue;
            }

            AssetMetadata metadata;
            try
            {
                metadata = ReadMetadata(metaPath);
            }
            catch when (!isCanonicalOutput)
            {
                continue;
            }
            GeneratedAssetMetadata? generated = metadata.Generated;
            bool owned = generated != null &&
                string.Equals(generated.ChildKind, TerrainTileIdentity.ChildKind, StringComparison.Ordinal) &&
                (generated.SourceGuid == desiredRootGuid ||
                 (existingRootGuid.HasValue && generated.SourceGuid == existingRootGuid.Value));
            if (!owned)
            {
                if (isCanonicalOutput)
                {
                    throw new InvalidOperationException(
                        $"[TerrainImportPlanner] Refusing terrain output directory '{generatedDirectory}' because '{metaPath}' is not owned by this terrain root.");
                }

                continue;
            }

            string normalizedSource = NormalizePath(sourcePath);
            observedSources.Add(normalizedSource);
            if (!expectedByPath.TryGetValue(normalizedSource, out TerrainTileImportPreview? expected) ||
                expected.Guid != metadata.Guid ||
                generated!.SourceGuid != desiredRootGuid ||
                !string.Equals(
                    generated.ChildKey,
                    TerrainTileIdentity.CreateChildKey(expected.Coordinate),
                    StringComparison.Ordinal))
            {
                obsoletePaths.Add(sourcePath);
                obsoletePaths.Add(metaPath);
                if (metadata.Guid != Guid.Empty && !desiredGuids.Contains(metadata.Guid))
                {
                    obsoleteGuids.Add(metadata.Guid);
                }
            }
        }

        foreach (string sourcePath in Directory.Exists(generatedDirectory)
                     ? Directory.EnumerateFiles(
                         generatedDirectory,
                         "*.ariterraingenerated",
                         SearchOption.AllDirectories)
                     : Array.Empty<string>())
        {
            if (!observedSources.Contains(NormalizePath(sourcePath)))
            {
                throw new InvalidOperationException(
                    $"[TerrainImportPlanner] Generated terrain source has no metadata: '{sourcePath}'.");
            }
        }

        return new GeneratedOutputInspection(
            obsoletePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            obsoleteGuids.Order().ToArray());
    }

    private static void ValidateGeneratedSourceOutputOwnership(
        string sourceAssetPath,
        Guid? existingRootGuid,
        Guid desiredRootGuid,
        string childKind,
        string diagnosticName,
        bool allowUntrackedSource,
        bool allowLegacyOwnedSource,
        string expectedAssetType,
        string expectedImporter)
    {
        bool hasSource = File.Exists(sourceAssetPath);
        bool hasMetadata = File.Exists(sourceAssetPath + ".meta");
        if (!hasSource && !hasMetadata)
        {
            return;
        }

        if (hasSource && !hasMetadata && allowUntrackedSource)
        {
            return;
        }

        if (hasSource != hasMetadata)
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] {diagnosticName} output collision is incomplete: '{sourceAssetPath}'.");
        }

        AssetMetadata metadata = ReadMetadata(sourceAssetPath + ".meta");
        GeneratedAssetMetadata? generated = metadata.Generated;
        bool owned = generated != null &&
            string.Equals(generated.ChildKind, childKind, StringComparison.Ordinal) &&
            (generated.SourceGuid == desiredRootGuid ||
             (existingRootGuid.HasValue && generated.SourceGuid == existingRootGuid.Value));
        owned |= allowLegacyOwnedSource &&
            generated == null &&
            metadata.Guid != Guid.Empty &&
            string.Equals(metadata.AssetType, expectedAssetType, StringComparison.Ordinal) &&
            string.Equals(metadata.Importer, expectedImporter, StringComparison.Ordinal);
        if (!owned)
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Refusing to overwrite foreign {diagnosticName} output '{sourceAssetPath}'.");
        }
    }

    private static IReadOnlyList<string> BuildDiagnostics(
        ExistingTerrainRoot? existing,
        WorldPartitionSettings? partition,
        IReadOnlyList<TerrainTileImportPreview> tiles,
        TerrainImportDestructiveChange changes,
        int obsoleteOutputCount)
    {
        var diagnostics = new List<string>();
        diagnostics.Add(existing == null
            ? "A new stable terrain-root identity will be created."
            : "The existing terrain-root identity will be preserved unless identity regeneration was requested.");
        if (partition == null)
        {
            diagnostics.Add("No active world partition was supplied; tile-to-cell ownership is unavailable in this preview.");
        }
        else
        {
            int boundaryTiles = tiles.Count(tile => tile.IntersectingCells.Count > 1);
            diagnostics.Add(
                boundaryTiles == 0
                    ? "Every terrain tile is contained by one world cell."
                    : $"{boundaryTiles} terrain tile(s) cross world-cell boundaries; owner cells use tile centers.");
        }

        if (changes != TerrainImportDestructiveChange.None)
        {
            diagnostics.Add(
                $"Explicit regeneration confirmation is required for: {changes}.");
        }

        if (obsoleteOutputCount > 0)
        {
            diagnostics.Add(
                $"Commit will transactionally remove {obsoleteOutputCount} obsolete generated file(s).");
        }

        return diagnostics;
    }

    private static string BuildRootSource(
        Guid rootGuid,
        string displayName,
        WorldPosition placement,
        TerrainSampleSpacing sampleSpacing,
        TerrainHeightRange heightRange,
        string authoredHeightPath,
        string authoredWeightPath,
        int tileResolution,
        TerrainTileCoordinate tileOrigin,
        AssetRef<TerrainLayerSetSourceAsset> layerSet,
        IReadOnlyList<TerrainGeneratedTileRecord> generatedTiles)
    {
        var text = new StringBuilder(512 + (generatedTiles.Count * 112));
        text.AppendLine($"Version: {TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion}");
        text.AppendLine($"TerrainGuid: {rootGuid:D}");
        text.AppendLine($"Name: {Quote(displayName)}");
        text.AppendLine(
            $"WorldPlacement: {{ X: {Number(placement.X)}, Y: {Number(placement.Y)}, Z: {Number(placement.Z)} }}");
        text.AppendLine(
            $"SampleSpacing: {{ X: {Number(sampleSpacing.X)}, Z: {Number(sampleSpacing.Z)} }}");
        text.AppendLine(
            $"HeightRange: {{ Min: {Number(heightRange.Min)}, Max: {Number(heightRange.Max)} }}");
        text.AppendLine("HeightSource:");
        text.AppendLine($"  Path: {Quote(authoredHeightPath)}");
        text.AppendLine($"  Format: {TerrainHeightSourceFormat.Pgm16BigEndianScalar}");
        text.AppendLine("WeightSource:");
        text.AppendLine($"  Path: {Quote(authoredWeightPath)}");
        text.AppendLine($"  Format: {TerrainWeightSourceFormat.Rgba8Hex}");
        text.AppendLine($"TileResolution: {tileResolution}");
        text.AppendLine($"BorderPolicy: {TerrainBorderPolicy.SharedEdgeSamples}");
        text.AppendLine($"TileOrigin: {{ X: {tileOrigin.X}, Z: {tileOrigin.Z} }}");
        text.AppendLine("LayerSet:");
        text.AppendLine($"  Guid: {layerSet.Guid:D}");
        text.AppendLine($"  PackageId: {Quote(layerSet.PackageId)}");
        text.AppendLine("GeneratedTiles:");
        foreach (TerrainGeneratedTileRecord tile in generatedTiles)
        {
            text.AppendLine(
                $"- Coordinate: {{ X: {tile.Coordinate.X}, Z: {tile.Coordinate.Z} }}");
            text.AppendLine($"  Guid: {tile.Guid:D}");
        }

        return text.ToString();
    }

    private static string BuildMetadata(
        Guid guid,
        string assetType,
        string importer,
        GeneratedAssetMetadata? generated)
    {
        var text = new StringBuilder(256);
        text.AppendLine($"Guid: {guid:D}");
        text.AppendLine($"AssetType: {Quote(assetType)}");
        text.AppendLine($"Importer: {Quote(importer)}");
        if (generated != null)
        {
            text.AppendLine("Generated:");
            text.AppendLine($"  SourceGuid: {generated.SourceGuid:D}");
            text.AppendLine($"  SourcePackageId: {Quote(generated.SourcePackageId)}");
            text.AppendLine($"  ChildKind: {Quote(generated.ChildKind)}");
            text.AppendLine($"  ChildKey: {Quote(generated.ChildKey)}");
            text.AppendLine($"  GeneratedByImporter: {Quote(generated.GeneratedByImporter)}");
        }

        return text.ToString();
    }

    private static string BuildFingerprint(
        TerrainImportRequest request,
        AssetRecord layerSetAsset,
        WorldPartitionSettings? partition,
        Guid rootGuid,
        string sourcePath,
        string rootAssetPath,
        string rootMetadataPath,
        string heightAssetPath,
        string weightAssetPath,
        string generatedDirectory,
        string rootSource,
        TerrainImportDestructiveChange changes)
    {
        var state = new StringBuilder(2_048);
        state.AppendLine(rootGuid.ToString("D"));
        state.AppendLine(((int)changes).ToString(CultureInfo.InvariantCulture));
        state.AppendLine(request.PackageAssetsRoot);
        state.AppendLine(request.PackageId);
        state.AppendLine(request.OutputDirectory);
        state.AppendLine(request.AssetName);
        state.AppendLine(request.RegenerateRootIdentity ? "1" : "0");
        state.AppendLine(rootSource);
        AppendPartition(state, partition);

        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            sourcePath,
            layerSetAsset.SourcePath,
            layerSetAsset.MetaPath,
            rootAssetPath,
            rootMetadataPath,
            heightAssetPath,
            heightAssetPath + ".meta",
            weightAssetPath,
            weightAssetPath + ".meta"
        };
        if (Directory.Exists(generatedDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(
                         generatedDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                observedPaths.Add(path);
            }
        }

        foreach (string path in observedPaths
                     .Select(NormalizePath)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            state.Append(path).Append('|');
            if (!File.Exists(path))
            {
                state.AppendLine("missing");
                continue;
            }

            state.AppendLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }

    private static void AppendPartition(
        StringBuilder state,
        WorldPartitionSettings? partition)
    {
        if (partition == null)
        {
            state.AppendLine("no-partition");
            return;
        }

        state.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"partition:{partition.Origin.X:R},{partition.Origin.Y:R},{partition.Origin.Z:R};" +
            $"{partition.CellSize.X:R},{partition.CellSize.Y:R},{partition.CellSize.Z:R}"));
    }

    private static void ValidateLayerSet(
        AssetRef<TerrainLayerSetSourceAsset> layerSet,
        AssetRecord layerSetAsset)
    {
        if (!layerSet.IsValid ||
            layerSet.Guid != layerSetAsset.Guid ||
            !string.Equals(layerSetAsset.AssetType, TerrainAssetTypes.LayerSet, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(layerSet.PackageId, layerSetAsset.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "[TerrainImportPlanner] Selected layer-set reference does not match its indexed source asset.",
                nameof(layerSet));
        }
    }

    private static void ValidateWorldBounds(WorldBounds bounds)
    {
        if (!bounds.IsValid)
        {
            throw new ArgumentException(
                "[TerrainImportPlanner] Terrain world bounds must be finite and strictly ordered.",
                nameof(bounds));
        }
    }

    private static void ValidateWorldPartition(WorldPartitionSettings? partition)
    {
        if (partition == null)
        {
            return;
        }

        if (!partition.Origin.IsFinite ||
            !partition.CellSize.IsFinite ||
            partition.CellSize.X <= 0.0 ||
            partition.CellSize.Y <= 0.0 ||
            partition.CellSize.Z <= 0.0)
        {
            throw new ArgumentException(
                "[TerrainImportPlanner] World partition origin/cell size is invalid.",
                nameof(partition));
        }
    }

    private static void ValidateTileLayout(int width, int height, int tileResolution)
    {
        int intervals = tileResolution - 1;
        if (tileResolution < TerrainRootSourceAssetLoader.MinTileResolution ||
            tileResolution > TerrainRootSourceAssetLoader.MaxTileResolution ||
            intervals <= 0 ||
            (intervals & (intervals - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileResolution),
                $"Terrain tile resolution must be 2^n + 1 within {TerrainRootSourceAssetLoader.MinTileResolution}..{TerrainRootSourceAssetLoader.MaxTileResolution}.");
        }

        if (width < tileResolution ||
            height < tileResolution ||
            (width - 1) % intervals != 0 ||
            (height - 1) % intervals != 0)
        {
            throw new InvalidDataException(
                $"[TerrainImportPlanner] Height dimensions {width}x{height} are not exact shared-edge multiples of tile resolution {tileResolution}.");
        }
    }

    private static string ValidateAssetsRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Terrain import requires a package Assets root.", nameof(path));
        }

        string fullPath = NormalizePath(path);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                "Assets",
                StringComparison.OrdinalIgnoreCase) ||
            ContainsPathSegment(fullPath, ".arisen"))
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Output root must be a package/workspace Assets directory, not '{fullPath}'.");
        }

        return fullPath;
    }

    private static string ResolveOutputDirectory(string assetsRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Terrain output directory must be a non-empty path relative to the package Assets root.",
                nameof(relativePath));
        }

        string output = NormalizePath(Path.Combine(assetsRoot, relativePath));
        if (!IsSameOrChildPath(output, assetsRoot) ||
            PathsEqual(output, assetsRoot) ||
            ContainsPathSegment(output, ".arisen"))
        {
            throw new InvalidOperationException(
                $"[TerrainImportPlanner] Terrain output directory must stay below '{assetsRoot}', not '{output}'.");
        }

        return output;
    }

    private static string ValidateHeightSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Terrain import requires a PGM height source.", nameof(path));
        }

        string fullPath = NormalizePath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".pgm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"[TerrainImportPlanner] Height source must use the explicit '.pgm' extension: '{fullPath}'.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Terrain import height source was not found.",
                fullPath);
        }

        return fullPath;
    }

    private static string ValidateAssetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value is "." or ".." ||
            value.EndsWith(".", StringComparison.Ordinal) ||
            value.EndsWith(" ", StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Terrain asset name must be one valid, trimmed file-name segment.",
                nameof(value));
        }

        return value;
    }

    private static string NormalizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Terrain display name must be non-empty canonical text.",
                nameof(value));
        }

        return value;
    }

    private static string NormalizePackageId(string value)
    {
        return TerrainSourceValidation.NormalizePackageId(
            value,
            "terrain import package id");
    }

    private static AssetMetadata ReadMetadata(string path)
    {
        try
        {
            return SerializationUtil.Deserialize<AssetMetadata>(
                path,
                serializeIfNotExist: false);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"[TerrainImportPlanner] Could not read metadata '{path}': {ex.Message}",
                ex);
        }
    }

    private static void AddUtf8Write(
        ICollection<TerrainImportFileWrite> writes,
        string path,
        string text)
    {
        writes.Add(new TerrainImportFileWrite(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text)));
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static bool ContainsPathSegment(string path, string segment)
    {
        return NormalizePath(path)
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrChildPath(string path, string parent)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedParent = NormalizePath(parent);
        return PathsEqual(normalizedPath, normalizedParent) ||
            normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record ExistingTerrainRoot(
        Guid Guid,
        TerrainRootSourceDescriptor Descriptor);

    private sealed record GeneratedOutputInspection(
        IReadOnlyList<string> ObsoletePaths,
        IReadOnlyList<Guid> ObsoleteTileGuids);
}

public static class TerrainImportEmitter
{
    public static TerrainImportCommitResult Commit(
        TerrainImportPlan plan,
        TerrainImportCommitOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TerrainImportPlan fresh = TerrainImportPlanner.CreatePlan(
            plan.Request,
            plan.LayerSetAsset,
            plan.WorldPartition);
        if (!string.Equals(fresh.Fingerprint, plan.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "[TerrainImportEmitter] Terrain preview is stale. Preview again before committing source changes.");
        }

        if (fresh.RequiresRegenerationConfirmation &&
            !options.ConfirmDestructiveRegeneration)
        {
            throw new InvalidOperationException(
                $"[TerrainImportEmitter] Commit requires explicit regeneration confirmation for: {fresh.DestructiveChanges}.");
        }

        var writtenPaths = fresh.Writes
            .Select(write => Path.GetFullPath(write.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var writeSet = new HashSet<string>(writtenPaths, StringComparer.OrdinalIgnoreCase);
        string[] removedPaths = fresh.ObsoletePaths
            .Select(Path.GetFullPath)
            .Where(path => !writeSet.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ExecuteTransaction(
            fresh.Request.PackageAssetsRoot,
            fresh.Writes,
            removedPaths);

        return new TerrainImportCommitResult(
            fresh.RootGuid,
            fresh.PreviousRootGuid is { } previous && previous != fresh.RootGuid
                ? previous
                : null,
            fresh.RootAssetPath,
            fresh.Tiles.Select(tile => tile.Guid).ToArray(),
            fresh.ObsoleteTileGuids,
            writtenPaths,
            removedPaths,
            fresh.ReplacesExistingRoot);
    }

    internal static void ExecuteTransaction(
        string assetsRoot,
        IReadOnlyList<TerrainImportFileWrite> writes,
        IReadOnlyList<string> deletes)
    {
        string normalizedAssetsRoot = Path.GetFullPath(assetsRoot);
        string transactionRoot = Path.Combine(
            normalizedAssetsRoot,
            ".terrain-import-" + Guid.NewGuid().ToString("N"));
        string stagedRoot = Path.Combine(transactionRoot, "staged");
        string backupRoot = Path.Combine(transactionRoot, "backup");
        var affected = writes.Select(write => Path.GetFullPath(write.Path))
            .Concat(deletes.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installed = new List<string>(writes.Count);
        var backups = new List<(string Original, string Backup)>();

        try
        {
            Directory.CreateDirectory(stagedRoot);
            Directory.CreateDirectory(backupRoot);
            foreach (TerrainImportFileWrite write in writes)
            {
                string target = Path.GetFullPath(write.Path);
                string relative = ValidateTransactionPath(normalizedAssetsRoot, target);
                string staged = Path.Combine(stagedRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, write.Bytes);
            }

            foreach (string target in affected)
            {
                string relative = ValidateTransactionPath(normalizedAssetsRoot, target);
                if (!File.Exists(target))
                {
                    continue;
                }

                string backup = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup);
                backups.Add((target, backup));
            }

            foreach (TerrainImportFileWrite write in writes
                         .OrderByDescending(write => write.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                string target = Path.GetFullPath(write.Path);
                string relative = ValidateTransactionPath(normalizedAssetsRoot, target);
                string staged = Path.Combine(stagedRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staged, target);
                installed.Add(target);
            }
        }
        catch (Exception commitError)
        {
            var rollbackErrors = new List<Exception>();
            for (int index = installed.Count - 1; index >= 0; index--)
            {
                string installedPath = installed[index];
                CaptureRollbackFailure(
                    () =>
                    {
                        if (File.Exists(installedPath))
                        {
                            File.Delete(installedPath);
                        }
                    },
                    $"delete installed file '{installedPath}'",
                    rollbackErrors);
            }

            for (int index = backups.Count - 1; index >= 0; index--)
            {
                (string original, string backup) = backups[index];
                if (!File.Exists(backup))
                {
                    continue;
                }

                CaptureRollbackFailure(
                    () =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                        File.Move(backup, original, overwrite: true);
                    },
                    $"restore backup '{original}'",
                    rollbackErrors);
            }

            CaptureRollbackFailure(
                () =>
                {
                    if (Directory.Exists(transactionRoot))
                    {
                        Directory.Delete(transactionRoot, recursive: true);
                    }
                },
                $"remove transaction directory '{transactionRoot}'",
                rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, commitError);
                throw new AggregateException(
                    "[TerrainImportEmitter] Terrain source transaction failed and rollback was incomplete.",
                    rollbackErrors);
            }

            throw;
        }

        TryDeleteDirectory(transactionRoot);
        TryPruneEmptyDirectories(deletes, normalizedAssetsRoot);
    }

    private static string ValidateTransactionPath(string assetsRoot, string target)
    {
        string relative = Path.GetRelativePath(assetsRoot, target);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, ".arisen", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"[TerrainImportEmitter] Transaction path escapes package Assets root: '{target}'.");
        }

        return relative;
    }

    private static void TryPruneEmptyDirectories(
        IReadOnlyList<string> removedPaths,
        string assetsRoot)
    {
        foreach (string path in removedPaths)
        {
            try
            {
                DirectoryInfo? directory = new FileInfo(path).Directory;
                while (directory != null &&
                       !string.Equals(directory.FullName, assetsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (!directory.Exists || directory.EnumerateFileSystemInfos().Any())
                    {
                        break;
                    }

                    DirectoryInfo? parent = directory.Parent;
                    directory.Delete();
                    directory = parent;
                }
            }
            catch
            {
                // Source files are already committed; empty-directory cleanup is best-effort.
            }
        }
    }

    private static void CaptureRollbackFailure(
        Action action,
        string operation,
        ICollection<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add(new IOException(
                $"[TerrainImportEmitter] Failed to {operation} during rollback.",
                ex));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
