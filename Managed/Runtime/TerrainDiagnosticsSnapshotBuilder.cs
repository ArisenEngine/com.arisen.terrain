using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public static class TerrainDiagnosticsSnapshotBuilder
{
    public const int MaximumRootCount = 256;
    public const int MaximumTileCount = 4096;
    public const int MaximumPatchCount = 16384;
    public const int MaximumResourceCount = 8192;
    public const int MaximumOwnersPerResource = 16;
    private const int MaximumDiagnosticLength = 512;

    public static TerrainDiagnosticsSnapshot Build(
        uint frameIndex,
        TerrainResidencyMetrics residency,
        TerrainLodMetrics lod,
        in WorldPosition queryPosition,
        in TerrainQueryResult query,
        IReadOnlyList<TerrainDiagnosticRootInput> rootInputs,
        IReadOnlyList<TerrainDiagnosticTileInput> tileInputs,
        ReadOnlySpan<TerrainPatchRecord> patches,
        IReadOnlyList<TerrainResidencyResourceSnapshot> resources)
    {
        ArgumentNullException.ThrowIfNull(residency);
        ArgumentNullException.ThrowIfNull(rootInputs);
        ArgumentNullException.ThrowIfNull(tileInputs);
        ArgumentNullException.ThrowIfNull(resources);

        TerrainDiagnosticRootInput[] orderedRoots = rootInputs
            .Where(input => input?.Root != null)
            .OrderBy(input => input.Root.Guid)
            .Take(MaximumRootCount)
            .ToArray();
        TerrainDiagnosticTileInput[] orderedTiles = tileInputs
            .Where(input => input != null && input.Reference.Guid != Guid.Empty)
            .OrderBy(input => input.Root?.Guid ?? input.Tile?.RootGuid ?? Guid.Empty)
            .ThenBy(input => input.Reference.Coordinate)
            .ThenBy(input => input.Reference.Guid)
            .Take(MaximumTileCount)
            .ToArray();

        TerrainPatchRecord[] orderedPatches = CopyOrderedPatches(patches);
        var patchesByTile = new Dictionary<Guid, List<TerrainPatchDiagnosticSnapshot>>();
        for (int index = 0; index < orderedPatches.Length; index++)
        {
            ref readonly TerrainPatchRecord patch = ref orderedPatches[index];
            if (!patchesByTile.TryGetValue(
                    patch.TileGuid,
                    out List<TerrainPatchDiagnosticSnapshot>? tilePatches))
            {
                tilePatches = new List<TerrainPatchDiagnosticSnapshot>();
                patchesByTile.Add(patch.TileGuid, tilePatches);
            }

            tilePatches.Add(new TerrainPatchDiagnosticSnapshot(
                patch.PatchKey,
                patch.LodLevel,
                patch.SampleStep,
                patch.StitchMask,
                patch.GeometricError,
                patch.ScreenSpaceError,
                patch.WorldBounds));
        }

        var tilesByGuid = new Dictionary<Guid, TerrainDiagnosticTileInput>();
        for (int index = 0; index < orderedTiles.Length; index++)
        {
            tilesByGuid.TryAdd(orderedTiles[index].Reference.Guid, orderedTiles[index]);
        }

        var tileSnapshots = new TerrainTileDiagnosticSnapshot[orderedTiles.Length];
        int seamViolationCount = 0;
        for (int index = 0; index < orderedTiles.Length; index++)
        {
            TerrainDiagnosticTileInput input = orderedTiles[index];
            TerrainTileNeighborDiagnostics neighbors = BuildNeighborDiagnostics(input, tilesByGuid);
            TerrainPatchDiagnosticSnapshot[] tilePatches = patchesByTile.TryGetValue(
                    input.Reference.Guid,
                    out List<TerrainPatchDiagnosticSnapshot>? selectedPatches)
                ? selectedPatches.ToArray()
                : Array.Empty<TerrainPatchDiagnosticSnapshot>();
            if (neighbors.PositiveX.IsViolation) seamViolationCount++;
            if (neighbors.PositiveZ.IsViolation) seamViolationCount++;

            CookedTerrainTile? tile = input.Tile;
            CookedTerrainRoot? root = input.Root;
            Guid rootGuid = root?.Guid ?? tile?.RootGuid ?? Guid.Empty;
            Guid layerSetGuid = root?.LayerSetGuid ?? tile?.LayerSetGuid ?? Guid.Empty;
            string packageId = root?.PackageId ?? tile?.PackageId ?? string.Empty;
            int sourceVersion = root?.SourceSchemaVersion ?? tile?.SourceSchemaVersion ?? 0;
            int resolution = tile?.Resolution ?? root?.TileResolution ?? 0;
            int layerCount = tile?.LayerCount ?? root?.Layers.Count ?? 0;
            WorldPosition placement = ResolveTilePlacement(input);
            TerrainSampleSpacing spacing = tile?.SampleSpacing ?? root?.SampleSpacing ?? default;
            double minHeight = tile?.MinHeight ?? input.Reference.MinHeight;
            double maxHeight = tile?.MaxHeight ?? input.Reference.MaxHeight;
            double width = resolution > 1 ? (resolution - 1) * spacing.X : 0.0;
            double depth = resolution > 1 ? (resolution - 1) * spacing.Z : 0.0;
            var bounds = new TerrainPatchWorldBounds(
                new WorldPosition(placement.X, placement.Y + minHeight, placement.Z),
                new WorldPosition(
                    placement.X + Math.Max(0.0, width),
                    placement.Y + maxHeight,
                    placement.Z + Math.Max(0.0, depth)));
            double maximumError = tile?.GeometricErrors.Count > 0
                ? tile.GeometricErrors.Max(level => level.MaxError)
                : 0.0;
            int minimumLod = tilePatches.Length == 0
                ? -1
                : tilePatches.Min(patch => patch.LodLevel);
            int maximumLod = tilePatches.Length == 0
                ? -1
                : tilePatches.Max(patch => patch.LodLevel);
            bool failed = input.ResidencyState == RuntimePreparedAssetState.Failed ||
                          (input.IsDirty && !string.IsNullOrWhiteSpace(input.Diagnostic));

            tileSnapshots[index] = new TerrainTileDiagnosticSnapshot(
                rootGuid,
                input.Reference.Guid,
                layerSetGuid,
                packageId,
                input.Reference.Coordinate,
                TerrainTileAssetCooker.CookedFormatVersion,
                sourceVersion,
                input.Generation,
                resolution,
                layerCount,
                bounds,
                minHeight,
                maxHeight,
                maximumError,
                minimumLod,
                maximumLod,
                input.CpuHeightBytes,
                input.CpuWeightBytes,
                input.CpuErrorBytes,
                input.PreparedGpuBytes,
                input.ResidencyState,
                input.IsVisible,
                input.IsDirty,
                failed,
                neighbors,
                CopyOwners(input.Owners),
                Array.AsReadOnly(tilePatches),
                BoundDiagnostic(input.Diagnostic));
        }

        var rootSnapshots = new TerrainRootDiagnosticSnapshot[orderedRoots.Length];
        for (int index = 0; index < orderedRoots.Length; index++)
        {
            TerrainDiagnosticRootInput input = orderedRoots[index];
            CookedTerrainRoot root = input.Root;
            double minHeight = root.Tiles.Count > 0
                ? root.Tiles.Min(tile => tile.MinHeight)
                : root.HeightRange.Min;
            double maxHeight = root.Tiles.Count > 0
                ? root.Tiles.Max(tile => tile.MaxHeight)
                : root.HeightRange.Max;
            var rootBounds = new TerrainPatchWorldBounds(
                new WorldPosition(
                    root.WorldPlacement.X,
                    root.WorldPlacement.Y + minHeight,
                    root.WorldPlacement.Z),
                new WorldPosition(
                    root.WorldPlacement.X + ((root.HeightSourceWidth - 1) * root.SampleSpacing.X),
                    root.WorldPlacement.Y + maxHeight,
                    root.WorldPlacement.Z + ((root.HeightSourceHeight - 1) * root.SampleSpacing.Z)));
            TerrainLayerDiagnosticSnapshot[] layers = root.Layers
                .Take(TerrainCookedFormat.WeightChannelCount)
                .Select((layer, layerIndex) => new TerrainLayerDiagnosticSnapshot(
                    layerIndex,
                    layer.Id,
                    layer.Albedo.Guid,
                    layer.Normal.Guid,
                    layer.Orm.Guid))
                .ToArray();
            int residentTileCount = tileSnapshots.Count(tile =>
                tile.TerrainRootGuid == root.Guid && tile.Generation != 0);
            bool failed = input.ResidencyState == RuntimePreparedAssetState.Failed ||
                          (input.IsDirty && !string.IsNullOrWhiteSpace(input.Diagnostic));

            rootSnapshots[index] = new TerrainRootDiagnosticSnapshot(
                root.Guid,
                root.LayerSetGuid,
                root.PackageId,
                root.Name,
                TerrainRootAssetCooker.CookedFormatVersion,
                root.SourceSchemaVersion,
                input.Generation,
                rootBounds,
                root.Tiles.Count,
                residentTileCount,
                input.CpuCookedBytes,
                input.PreparedGpuBytes,
                input.ResidencyState,
                input.IsDirty,
                failed,
                CopyOwners(input.Owners),
                Array.AsReadOnly(layers),
                BoundDiagnostic(input.Diagnostic));
        }

        TerrainResidencyResourceSnapshot[] boundedResources = resources
            .OrderBy(resource => resource.Key)
            .Take(MaximumResourceCount)
            .Select(resource => resource with
            {
                Owners = CopyOwners(resource.Owners),
                Diagnostic = BoundDiagnostic(resource.Diagnostic)
            })
            .ToArray();

        return new TerrainDiagnosticsSnapshot(
            frameIndex,
            residency,
            lod,
            queryPosition,
            query,
            Array.AsReadOnly(rootSnapshots),
            Array.AsReadOnly(tileSnapshots),
            Array.AsReadOnly(boundedResources),
            seamViolationCount,
            Math.Max(0, rootInputs.Count - orderedRoots.Length),
            Math.Max(0, tileInputs.Count - orderedTiles.Length),
            Math.Max(0, patches.Length - orderedPatches.Length));
    }

    private static TerrainPatchRecord[] CopyOrderedPatches(ReadOnlySpan<TerrainPatchRecord> patches)
    {
        int count = Math.Min(patches.Length, MaximumPatchCount);
        var copy = new TerrainPatchRecord[count];
        patches[..count].CopyTo(copy);
        Array.Sort(copy, TerrainPatchComparer.Instance);
        return copy;
    }

    private static TerrainTileNeighborDiagnostics BuildNeighborDiagnostics(
        TerrainDiagnosticTileInput input,
        IReadOnlyDictionary<Guid, TerrainDiagnosticTileInput> tilesByGuid)
    {
        TerrainTileNeighborSet neighbors = input.Reference.Neighbors;
        return new TerrainTileNeighborDiagnostics(
            CompareEdge(input, neighbors.NegativeX, TerrainPatchEdge.NegativeX, tilesByGuid),
            CompareEdge(input, neighbors.PositiveX, TerrainPatchEdge.PositiveX, tilesByGuid),
            CompareEdge(input, neighbors.NegativeZ, TerrainPatchEdge.NegativeZ, tilesByGuid),
            CompareEdge(input, neighbors.PositiveZ, TerrainPatchEdge.PositiveZ, tilesByGuid));
    }

    private static TerrainNeighborDiagnosticSnapshot CompareEdge(
        TerrainDiagnosticTileInput input,
        Guid expectedGuid,
        TerrainPatchEdge edge,
        IReadOnlyDictionary<Guid, TerrainDiagnosticTileInput> tilesByGuid)
    {
        if (expectedGuid == Guid.Empty)
        {
            return new TerrainNeighborDiagnosticSnapshot(
                Guid.Empty,
                false,
                TerrainSeamDiagnosticState.Boundary,
                0);
        }

        if (!tilesByGuid.TryGetValue(expectedGuid, out TerrainDiagnosticTileInput? neighbor) ||
            input.Tile == null || neighbor.Tile == null)
        {
            return new TerrainNeighborDiagnosticSnapshot(
                expectedGuid,
                false,
                TerrainSeamDiagnosticState.NeighborUnavailable,
                0);
        }

        CookedTerrainTile tile = input.Tile;
        CookedTerrainTile other = neighbor.Tile;
        if (tile.RootGuid != other.RootGuid ||
            tile.LayerSetGuid != other.LayerSetGuid ||
            tile.Resolution != other.Resolution ||
            tile.SampleSpacing != other.SampleSpacing ||
            tile.HeightRange != other.HeightRange)
        {
            return new TerrainNeighborDiagnosticSnapshot(
                expectedGuid,
                true,
                TerrainSeamDiagnosticState.Incompatible,
                0);
        }

        int mismatchCount = CountHeightMismatches(tile, other, edge);
        return new TerrainNeighborDiagnosticSnapshot(
            expectedGuid,
            true,
            mismatchCount == 0
                ? TerrainSeamDiagnosticState.Valid
                : TerrainSeamDiagnosticState.HeightMismatch,
            mismatchCount);
    }

    private static int CountHeightMismatches(
        CookedTerrainTile tile,
        CookedTerrainTile neighbor,
        TerrainPatchEdge edge)
    {
        int last = tile.Resolution - 1;
        int mismatches = 0;
        for (int sample = 0; sample < tile.Resolution; sample++)
        {
            ushort left;
            ushort right;
            switch (edge)
            {
                case TerrainPatchEdge.NegativeX:
                    left = tile.GetHeightSample(0, sample);
                    right = neighbor.GetHeightSample(last, sample);
                    break;
                case TerrainPatchEdge.PositiveX:
                    left = tile.GetHeightSample(last, sample);
                    right = neighbor.GetHeightSample(0, sample);
                    break;
                case TerrainPatchEdge.NegativeZ:
                    left = tile.GetHeightSample(sample, 0);
                    right = neighbor.GetHeightSample(sample, last);
                    break;
                case TerrainPatchEdge.PositiveZ:
                    left = tile.GetHeightSample(sample, last);
                    right = neighbor.GetHeightSample(sample, 0);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edge));
            }

            if (left != right) mismatches++;
        }

        return mismatches;
    }

    private static WorldPosition ResolveTilePlacement(TerrainDiagnosticTileInput input)
    {
        if (input.Tile != null)
        {
            return input.Tile.WorldPlacement;
        }

        CookedTerrainRoot? root = input.Root;
        if (root == null)
        {
            return default;
        }

        int intervals = root.TileResolution - 1;
        int localX = input.Reference.Coordinate.X - root.TileOrigin.X;
        int localZ = input.Reference.Coordinate.Z - root.TileOrigin.Z;
        return new WorldPosition(
            root.WorldPlacement.X + (localX * intervals * root.SampleSpacing.X),
            root.WorldPlacement.Y,
            root.WorldPlacement.Z + (localZ * intervals * root.SampleSpacing.Z));
    }

    private static IReadOnlyList<RuntimeAssetResidencyOwnerId> CopyOwners(
        IReadOnlyList<RuntimeAssetResidencyOwnerId>? owners)
    {
        if (owners == null || owners.Count == 0)
        {
            return Array.Empty<RuntimeAssetResidencyOwnerId>();
        }

        RuntimeAssetResidencyOwnerId[] copy = owners
            .Order()
            .Take(MaximumOwnersPerResource)
            .ToArray();
        return Array.AsReadOnly(copy);
    }

    private static string BoundDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return string.Empty;
        }

        string trimmed = diagnostic.Trim();
        return trimmed.Length <= MaximumDiagnosticLength
            ? trimmed
            : trimmed[..MaximumDiagnosticLength];
    }

    private sealed class TerrainPatchComparer : IComparer<TerrainPatchRecord>
    {
        public static TerrainPatchComparer Instance { get; } = new();

        public int Compare(TerrainPatchRecord left, TerrainPatchRecord right)
        {
            int comparison = left.TerrainRootGuid.CompareTo(right.TerrainRootGuid);
            if (comparison != 0) return comparison;
            comparison = left.TileCoordinate.CompareTo(right.TileCoordinate);
            if (comparison != 0) return comparison;
            comparison = left.TileGuid.CompareTo(right.TileGuid);
            if (comparison != 0) return comparison;
            return left.PatchKey.CompareTo(right.PatchKey);
        }
    }
}
