using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public enum TerrainResidentResourceKind
{
    Root = 1,
    Tile = 2
}

public readonly record struct TerrainResidentResourceHandle(
    TerrainResidentResourceKind Kind,
    Guid Guid,
    ulong Generation)
{
    public bool IsValid => Guid != Guid.Empty && Generation != 0;
}

public readonly record struct TerrainRuntimePublication(
    TerrainResidentResourceHandle Root,
    IReadOnlyList<TerrainResidentResourceHandle> Tiles);

public readonly record struct TerrainRuntimeDataMetrics(
    int RootCount,
    int TileCount,
    long HeightBytes,
    long WeightBytes,
    long ErrorBytes);

public interface ITerrainRuntimeDataStore
{
    TerrainResidentResourceHandle PublishRoot(CookedTerrainRoot root);

    TerrainResidentResourceHandle PublishTile(CookedTerrainTile tile);

    TerrainRuntimePublication PublishReplacement(
        CookedTerrainRoot root,
        IReadOnlyList<CookedTerrainTile> tiles);

    bool Remove(TerrainResidentResourceHandle handle);

    TerrainRuntimeDataMetrics GetMetrics();
}

internal sealed class TerrainRuntimeDataStore : ITerrainRuntimeDataStore
{
    private Dictionary<Guid, TerrainResidentRootData> m_Roots = new();
    private Dictionary<Guid, TerrainResidentTileData> m_Tiles = new();
    private TerrainResidentRootData[] m_OrderedRoots = Array.Empty<TerrainResidentRootData>();
    private TerrainResidentTileData[] m_OrderedTiles = Array.Empty<TerrainResidentTileData>();
    private ulong m_NextGeneration;

    internal ReadOnlySpan<TerrainResidentRootData> Roots => m_OrderedRoots;

    internal ReadOnlySpan<TerrainResidentTileData> Tiles => m_OrderedTiles;

    public TerrainResidentResourceHandle PublishRoot(CookedTerrainRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var prepared = new TerrainResidentRootData(root, NextGeneration());
        foreach (TerrainResidentTileData tile in m_Tiles.Values)
        {
            if (tile.Tile.RootGuid == root.Guid)
            {
                prepared.ValidateTile(tile.Tile);
            }
        }

        m_Roots[root.Guid] = prepared;
        RebuildOrderedRoots();
        return new TerrainResidentResourceHandle(
            TerrainResidentResourceKind.Root,
            root.Guid,
            prepared.Generation);
    }

    public TerrainResidentResourceHandle PublishTile(CookedTerrainTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (m_Roots.TryGetValue(tile.RootGuid, out TerrainResidentRootData? root))
        {
            root.ValidateTile(tile);
        }

        foreach (TerrainResidentTileData resident in m_Tiles.Values)
        {
            if (resident.Tile.Guid != tile.Guid &&
                resident.Tile.RootGuid == tile.RootGuid &&
                resident.Tile.Coordinate == tile.Coordinate)
            {
                throw new InvalidOperationException(
                    $"Terrain root '{tile.RootGuid:D}' coordinate {tile.Coordinate} is already " +
                    $"published by tile '{resident.Tile.Guid:D}'.");
            }
        }

        var prepared = new TerrainResidentTileData(
            tile,
            NextGeneration(),
            TerrainTileAcceleration.Build(tile));
        m_Tiles[tile.Guid] = prepared;
        RebuildOrderedTiles();
        return new TerrainResidentResourceHandle(
            TerrainResidentResourceKind.Tile,
            tile.Guid,
            prepared.Generation);
    }

    public TerrainRuntimePublication PublishReplacement(
        CookedTerrainRoot root,
        IReadOnlyList<CookedTerrainTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tiles);

        var candidateRoots = new Dictionary<Guid, TerrainResidentRootData>(m_Roots);
        var candidateTiles = new Dictionary<Guid, TerrainResidentTileData>(m_Tiles);
        var replacementRoot = new TerrainResidentRootData(root, NextGeneration());
        candidateRoots[root.Guid] = replacementRoot;

        var tileHandles = new TerrainResidentResourceHandle[tiles.Count];
        var replacementGuids = new HashSet<Guid>();
        for (int index = 0; index < tiles.Count; index++)
        {
            CookedTerrainTile tile = tiles[index]
                ?? throw new ArgumentException(
                    "Terrain replacement tiles cannot contain null entries.",
                    nameof(tiles));
            if (tile.RootGuid != root.Guid || !replacementGuids.Add(tile.Guid))
            {
                throw new InvalidOperationException(
                    $"Terrain replacement for root '{root.Guid:D}' contains an invalid or " +
                    $"duplicate tile '{tile.Guid:D}'.");
            }

            replacementRoot.ValidateTile(tile);
            var replacementTile = new TerrainResidentTileData(
                tile,
                NextGeneration(),
                TerrainTileAcceleration.Build(tile));
            candidateTiles[tile.Guid] = replacementTile;
            tileHandles[index] = new TerrainResidentResourceHandle(
                TerrainResidentResourceKind.Tile,
                tile.Guid,
                replacementTile.Generation);
        }

        ValidateCandidateState(candidateRoots, candidateTiles);
        TerrainResidentRootData[] orderedRoots = BuildOrderedRoots(candidateRoots);
        TerrainResidentTileData[] orderedTiles = BuildOrderedTiles(candidateTiles);

        m_Roots = candidateRoots;
        m_Tiles = candidateTiles;
        m_OrderedRoots = orderedRoots;
        m_OrderedTiles = orderedTiles;
        return new TerrainRuntimePublication(
            new TerrainResidentResourceHandle(
                TerrainResidentResourceKind.Root,
                root.Guid,
                replacementRoot.Generation),
            tileHandles);
    }

    public bool Remove(TerrainResidentResourceHandle handle)
    {
        if (!handle.IsValid)
        {
            return false;
        }

        switch (handle.Kind)
        {
            case TerrainResidentResourceKind.Root:
                if (!m_Roots.TryGetValue(handle.Guid, out TerrainResidentRootData? root) ||
                    root.Generation != handle.Generation)
                {
                    return false;
                }

                m_Roots.Remove(handle.Guid);
                RebuildOrderedRoots();
                return true;

            case TerrainResidentResourceKind.Tile:
                if (!m_Tiles.TryGetValue(handle.Guid, out TerrainResidentTileData? tile) ||
                    tile.Generation != handle.Generation)
                {
                    return false;
                }

                m_Tiles.Remove(handle.Guid);
                RebuildOrderedTiles();
                return true;

            default:
                return false;
        }
    }

    public TerrainRuntimeDataMetrics GetMetrics()
    {
        long heightBytes = 0;
        long weightBytes = 0;
        long errorBytes = 0;
        for (int index = 0; index < m_OrderedTiles.Length; index++)
        {
            CookedTerrainTile tile = m_OrderedTiles[index].Tile;
            heightBytes = checked(heightBytes + (tile.Heights.Length * sizeof(ushort)));
            weightBytes = checked(weightBytes + tile.LayerWeights.Length);
            errorBytes = checked(
                errorBytes + (tile.GeometricErrors.Count * TerrainPreparedErrorRecordSize));
        }

        return new TerrainRuntimeDataMetrics(
            m_OrderedRoots.Length,
            m_OrderedTiles.Length,
            heightBytes,
            weightBytes,
            errorBytes);
    }

    internal bool TryGetTile(Guid tileGuid, out TerrainResidentTileData tile)
    {
        if (m_Tiles.TryGetValue(tileGuid, out TerrainResidentTileData? resident))
        {
            tile = resident;
            return true;
        }

        tile = null!;
        return false;
    }

    internal void Clear()
    {
        m_Roots.Clear();
        m_Tiles.Clear();
        m_OrderedRoots = Array.Empty<TerrainResidentRootData>();
        m_OrderedTiles = Array.Empty<TerrainResidentTileData>();
    }

    private ulong NextGeneration()
    {
        m_NextGeneration++;
        if (m_NextGeneration == 0)
        {
            m_NextGeneration++;
        }

        return m_NextGeneration;
    }

    private const int TerrainPreparedErrorRecordSize = 16;

    private void RebuildOrderedRoots()
    {
        m_OrderedRoots = BuildOrderedRoots(m_Roots);
    }

    private void RebuildOrderedTiles()
    {
        m_OrderedTiles = BuildOrderedTiles(m_Tiles);
    }

    private static void ValidateCandidateState(
        IReadOnlyDictionary<Guid, TerrainResidentRootData> roots,
        IReadOnlyDictionary<Guid, TerrainResidentTileData> tiles)
    {
        var occupiedCoordinates = new HashSet<(Guid RootGuid, TerrainTileCoordinate Coordinate)>();
        foreach (TerrainResidentTileData tile in tiles.Values)
        {
            if (!occupiedCoordinates.Add((tile.Tile.RootGuid, tile.Tile.Coordinate)))
            {
                throw new InvalidOperationException(
                    $"Terrain root '{tile.Tile.RootGuid:D}' coordinate " +
                    $"{tile.Tile.Coordinate} is published more than once.");
            }

            if (roots.TryGetValue(tile.Tile.RootGuid, out TerrainResidentRootData? root))
            {
                root.ValidateTile(tile.Tile);
            }
        }
    }

    private static TerrainResidentRootData[] BuildOrderedRoots(
        IReadOnlyDictionary<Guid, TerrainResidentRootData> roots)
    {
        TerrainResidentRootData[] ordered = roots.Values.ToArray();
        Array.Sort(ordered, TerrainResidentRootComparer.Instance);
        return ordered;
    }

    private static TerrainResidentTileData[] BuildOrderedTiles(
        IReadOnlyDictionary<Guid, TerrainResidentTileData> tiles)
    {
        TerrainResidentTileData[] ordered = tiles.Values.ToArray();
        Array.Sort(ordered, TerrainResidentTileComparer.Instance);
        return ordered;
    }

    private sealed class TerrainResidentRootComparer : IComparer<TerrainResidentRootData>
    {
        public static TerrainResidentRootComparer Instance { get; } = new();

        public int Compare(TerrainResidentRootData? left, TerrainResidentRootData? right) =>
            left!.Root.Guid.CompareTo(right!.Root.Guid);
    }

    private sealed class TerrainResidentTileComparer : IComparer<TerrainResidentTileData>
    {
        public static TerrainResidentTileComparer Instance { get; } = new();

        public int Compare(TerrainResidentTileData? left, TerrainResidentTileData? right)
        {
            int result = left!.Tile.RootGuid.CompareTo(right!.Tile.RootGuid);
            if (result != 0)
            {
                return result;
            }

            result = left.Tile.Coordinate.CompareTo(right.Tile.Coordinate);
            return result != 0 ? result : left.Tile.Guid.CompareTo(right.Tile.Guid);
        }
    }
}

internal sealed class TerrainResidentRootData
{
    private readonly Dictionary<TerrainTileCoordinate, CookedTerrainTileReference> m_Tiles;
    private readonly double m_MaxX;
    private readonly double m_MaxZ;

    public TerrainResidentRootData(CookedTerrainRoot root, ulong generation)
    {
        Root = root;
        Generation = generation;
        m_Tiles = new Dictionary<TerrainTileCoordinate, CookedTerrainTileReference>(
            root.Tiles.Count);
        for (int index = 0; index < root.Tiles.Count; index++)
        {
            CookedTerrainTileReference tile = root.Tiles[index];
            if (!m_Tiles.TryAdd(tile.Coordinate, tile))
            {
                throw new InvalidOperationException(
                    $"Terrain root '{root.Guid:D}' repeats coordinate {tile.Coordinate}.");
            }
        }

        m_MaxX = root.WorldPlacement.X +
                 ((root.HeightSourceWidth - 1) * root.SampleSpacing.X);
        m_MaxZ = root.WorldPlacement.Z +
                 ((root.HeightSourceHeight - 1) * root.SampleSpacing.Z);
        if (!double.IsFinite(m_MaxX) || !double.IsFinite(m_MaxZ) ||
            m_MaxX <= root.WorldPlacement.X || m_MaxZ <= root.WorldPlacement.Z)
        {
            throw new InvalidOperationException(
                $"Terrain root '{root.Guid:D}' has invalid world-space coverage.");
        }
    }

    public CookedTerrainRoot Root { get; }

    public ulong Generation { get; }

    public bool Contains(double worldX, double worldZ) =>
        worldX >= Root.WorldPlacement.X && worldX <= m_MaxX &&
        worldZ >= Root.WorldPlacement.Z && worldZ <= m_MaxZ;

    public bool TryResolveTile(
        double worldX,
        double worldZ,
        out CookedTerrainTileReference tile)
    {
        tile = null!;
        if (!Contains(worldX, worldZ))
        {
            return false;
        }

        int intervals = Root.TileResolution - 1;
        double tileSizeX = intervals * Root.SampleSpacing.X;
        double tileSizeZ = intervals * Root.SampleSpacing.Z;
        int tileCountX = (Root.HeightSourceWidth - 1) / intervals;
        int tileCountZ = (Root.HeightSourceHeight - 1) / intervals;
        int localTileX = Math.Min(
            tileCountX - 1,
            Math.Max(0, (int)Math.Floor((worldX - Root.WorldPlacement.X) / tileSizeX)));
        int localTileZ = Math.Min(
            tileCountZ - 1,
            Math.Max(0, (int)Math.Floor((worldZ - Root.WorldPlacement.Z) / tileSizeZ)));
        var coordinate = new TerrainTileCoordinate(
            checked(Root.TileOrigin.X + localTileX),
            checked(Root.TileOrigin.Z + localTileZ));
        return m_Tiles.TryGetValue(coordinate, out tile!);
    }

    public void ValidateTile(CookedTerrainTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!m_Tiles.TryGetValue(tile.Coordinate, out CookedTerrainTileReference? reference) ||
            reference.Guid != tile.Guid ||
            tile.RootGuid != Root.Guid ||
            tile.LayerSetGuid != Root.LayerSetGuid ||
            tile.LayerCount != Root.Layers.Count ||
            !string.Equals(tile.PackageId, Root.PackageId, StringComparison.Ordinal) ||
            tile.Resolution != Root.TileResolution ||
            tile.SampleSpacing != Root.SampleSpacing ||
            tile.HeightRange != Root.HeightRange ||
            tile.BorderPolicy != Root.BorderPolicy ||
            tile.MinHeight != reference.MinHeight ||
            tile.MaxHeight != reference.MaxHeight)
        {
            throw new InvalidOperationException(
                $"Terrain tile '{tile.Guid:D}' does not match resident root '{Root.Guid:D}'.");
        }

        int intervals = Root.TileResolution - 1;
        int localTileX = checked(tile.Coordinate.X - Root.TileOrigin.X);
        int localTileZ = checked(tile.Coordinate.Z - Root.TileOrigin.Z);
        var expectedPlacement = new WorldPosition(
            Root.WorldPlacement.X + (localTileX * intervals * Root.SampleSpacing.X),
            Root.WorldPlacement.Y,
            Root.WorldPlacement.Z + (localTileZ * intervals * Root.SampleSpacing.Z));
        if (tile.WorldPlacement != expectedPlacement)
        {
            throw new InvalidOperationException(
                $"Terrain tile '{tile.Guid:D}' placement {tile.WorldPlacement} does not match " +
                $"resident root placement {expectedPlacement}.");
        }
    }
}

internal sealed class TerrainResidentTileData
{
    public TerrainResidentTileData(
        CookedTerrainTile tile,
        ulong generation,
        TerrainTileAcceleration acceleration)
    {
        Tile = tile;
        Generation = generation;
        Acceleration = acceleration;
    }

    public CookedTerrainTile Tile { get; }

    public ulong Generation { get; }

    public TerrainTileAcceleration Acceleration { get; }
}

internal readonly struct TerrainPatchAcceleration
{
    public TerrainPatchAcceleration(
        int patchX,
        int patchZ,
        int sampleX,
        int sampleZ,
        int intervalCount,
        int errorOffset,
        double minHeight,
        double maxHeight)
    {
        PatchX = patchX;
        PatchZ = patchZ;
        SampleX = sampleX;
        SampleZ = sampleZ;
        IntervalCount = intervalCount;
        ErrorOffset = errorOffset;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
    }

    public int PatchX { get; }
    public int PatchZ { get; }
    public int SampleX { get; }
    public int SampleZ { get; }
    public int IntervalCount { get; }
    public int ErrorOffset { get; }
    public double MinHeight { get; }
    public double MaxHeight { get; }
}

internal sealed class TerrainTileAcceleration
{
    private readonly TerrainPatchAcceleration[] m_Patches;
    private readonly double[] m_GeometricErrors;

    private TerrainTileAcceleration(
        int patchCountX,
        int patchCountZ,
        int lodLevelCount,
        TerrainPatchAcceleration[] patches,
        double[] geometricErrors)
    {
        PatchCountX = patchCountX;
        PatchCountZ = patchCountZ;
        LodLevelCount = lodLevelCount;
        m_Patches = patches;
        m_GeometricErrors = geometricErrors;
    }

    public int PatchCountX { get; }

    public int PatchCountZ { get; }

    public int PatchCount => m_Patches.Length;

    public int LodLevelCount { get; }

    public ref readonly TerrainPatchAcceleration GetPatch(int index) =>
        ref m_Patches[index];

    public double GetGeometricError(int patchIndex, int lodLevel)
    {
        if ((uint)patchIndex >= (uint)m_Patches.Length ||
            (uint)lodLevel >= (uint)LodLevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(patchIndex));
        }

        return m_GeometricErrors[m_Patches[patchIndex].ErrorOffset + lodLevel];
    }

    public static TerrainTileAcceleration Build(CookedTerrainTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        int tileIntervals = tile.Resolution - 1;
        int patchIntervals = Math.Min(
            TerrainPatchTopology.MaximumPatchIntervalCount,
            tileIntervals);
        int patchCountX = tileIntervals / patchIntervals;
        int patchCountZ = tileIntervals / patchIntervals;
        int lodLevelCount = System.Numerics.BitOperations.Log2(
            checked((uint)patchIntervals)) + 1;
        int patchCount = checked(patchCountX * patchCountZ);
        var patches = new TerrainPatchAcceleration[patchCount];
        var errors = new double[checked(patchCount * lodLevelCount)];
        ReadOnlySpan<ushort> heights = tile.Heights.Span;

        int patchIndex = 0;
        for (int patchZ = 0; patchZ < patchCountZ; patchZ++)
        {
            for (int patchX = 0; patchX < patchCountX; patchX++)
            {
                int sampleX = patchX * patchIntervals;
                int sampleZ = patchZ * patchIntervals;
                double minimum = double.MaxValue;
                double maximum = double.MinValue;
                for (int localZ = 0; localZ <= patchIntervals; localZ++)
                {
                    int row = checked((sampleZ + localZ) * tile.Resolution);
                    for (int localX = 0; localX <= patchIntervals; localX++)
                    {
                        double height = tile.DecodeHeight(heights[row + sampleX + localX]);
                        minimum = Math.Min(minimum, height);
                        maximum = Math.Max(maximum, height);
                    }
                }

                int errorOffset = patchIndex * lodLevelCount;
                double previousError = 0.0;
                for (int lodLevel = 0; lodLevel < lodLevelCount; lodLevel++)
                {
                    int sampleStep = 1 << lodLevel;
                    double error = ComputeGeometricError(
                        tile,
                        heights,
                        sampleX,
                        sampleZ,
                        patchIntervals,
                        sampleStep);
                    error = Math.Max(previousError, error);
                    ValidateAgainstCookedError(tile, lodLevel, sampleStep, error);
                    errors[errorOffset + lodLevel] = error;
                    previousError = error;
                }

                patches[patchIndex] = new TerrainPatchAcceleration(
                    patchX,
                    patchZ,
                    sampleX,
                    sampleZ,
                    patchIntervals,
                    errorOffset,
                    minimum,
                    maximum);
                patchIndex++;
            }
        }

        return new TerrainTileAcceleration(
            patchCountX,
            patchCountZ,
            lodLevelCount,
            patches,
            errors);
    }

    private static double ComputeGeometricError(
        CookedTerrainTile tile,
        ReadOnlySpan<ushort> heights,
        int sampleX,
        int sampleZ,
        int intervalCount,
        int sampleStep)
    {
        if (sampleStep == 1)
        {
            return 0.0;
        }

        double maximumError = 0.0;
        for (int blockZ = 0; blockZ < intervalCount; blockZ += sampleStep)
        {
            for (int blockX = 0; blockX < intervalCount; blockX += sampleStep)
            {
                int x0 = sampleX + blockX;
                int z0 = sampleZ + blockZ;
                double h00 = Decode(tile, heights, x0, z0);
                double h10 = Decode(tile, heights, x0 + sampleStep, z0);
                double h01 = Decode(tile, heights, x0, z0 + sampleStep);
                double h11 = Decode(tile, heights, x0 + sampleStep, z0 + sampleStep);
                for (int localZ = 0; localZ <= sampleStep; localZ++)
                {
                    double tz = (double)localZ / sampleStep;
                    double left = h00 + ((h01 - h00) * tz);
                    double right = h10 + ((h11 - h10) * tz);
                    for (int localX = 0; localX <= sampleStep; localX++)
                    {
                        double tx = (double)localX / sampleStep;
                        double interpolated = left + ((right - left) * tx);
                        double actual = Decode(tile, heights, x0 + localX, z0 + localZ);
                        maximumError = Math.Max(
                            maximumError,
                            Math.Abs(actual - interpolated));
                    }
                }
            }
        }

        return maximumError;
    }

    private static double Decode(
        CookedTerrainTile tile,
        ReadOnlySpan<ushort> heights,
        int x,
        int z) =>
        tile.DecodeHeight(heights[checked((z * tile.Resolution) + x)]);

    private static void ValidateAgainstCookedError(
        CookedTerrainTile tile,
        int lodLevel,
        int sampleStep,
        double localError)
    {
        if ((uint)lodLevel >= (uint)tile.GeometricErrors.Count)
        {
            throw new InvalidDataException(
                $"Terrain tile '{tile.Guid:D}' is missing geometric-error level {lodLevel}.");
        }

        TerrainGeometricErrorLevel cooked = tile.GeometricErrors[lodLevel];
        double tolerance = Math.Max(1.0e-9, Math.Abs(cooked.MaxError) * 1.0e-12);
        if (cooked.Level != lodLevel ||
            cooked.SampleStep != sampleStep ||
            localError > cooked.MaxError + tolerance)
        {
            throw new InvalidDataException(
                $"Terrain tile '{tile.Guid:D}' patch error at level {lodLevel} is not " +
                "conservative with the cooked hierarchy.");
        }
    }
}
