using System.Numerics;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public enum TerrainLodProjection
{
    Perspective = 0,
    Orthographic = 1
}

public readonly record struct TerrainLodView(
    WorldPosition CameraWorldPosition,
    WorldPosition RenderOrigin,
    Matrix4x4 OriginRelativeViewProjection,
    TerrainLodProjection Projection,
    double VerticalFieldOfViewRadians,
    double OrthographicHeight,
    int ViewportHeight)
{
    public bool IsValid =>
        CameraWorldPosition.IsFinite &&
        RenderOrigin.IsFinite &&
        ViewportHeight > 0 &&
        IsFinite(OriginRelativeViewProjection) &&
        (Projection == TerrainLodProjection.Perspective
            ? double.IsFinite(VerticalFieldOfViewRadians) &&
              VerticalFieldOfViewRadians > 0.0 &&
              VerticalFieldOfViewRadians < Math.PI
            : Projection == TerrainLodProjection.Orthographic &&
              double.IsFinite(OrthographicHeight) &&
              OrthographicHeight > 0.0);

    public double ProjectionScale => Projection == TerrainLodProjection.Perspective
        ? ViewportHeight / (2.0 * Math.Tan(VerticalFieldOfViewRadians * 0.5))
        : ViewportHeight / OrthographicHeight;

    private static bool IsFinite(in Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}

public readonly record struct TerrainLodSettings(
    double MaximumScreenSpaceError,
    double HysteresisFraction,
    int MaximumPatchCount,
    bool EnableFrustumCulling)
{
    public static TerrainLodSettings Default { get; } = new(
        MaximumScreenSpaceError: 2.0,
        HysteresisFraction: 0.15,
        MaximumPatchCount: 16_384,
        EnableFrustumCulling: true);

    public bool IsValid =>
        double.IsFinite(MaximumScreenSpaceError) &&
        MaximumScreenSpaceError > 0.0 &&
        double.IsFinite(HysteresisFraction) &&
        HysteresisFraction >= 0.0 &&
        HysteresisFraction < 0.5 &&
        MaximumPatchCount > 0;
}

public readonly record struct TerrainPatchKey(int X, int Z) : IComparable<TerrainPatchKey>
{
    public int CompareTo(TerrainPatchKey other)
    {
        int result = Z.CompareTo(other.Z);
        return result != 0 ? result : X.CompareTo(other.X);
    }
}

[Flags]
public enum TerrainPatchStitchMask : byte
{
    None = 0,
    NegativeX = 1 << 0,
    PositiveX = 1 << 1,
    NegativeZ = 1 << 2,
    PositiveZ = 1 << 3
}

public enum TerrainPatchEdge
{
    NegativeX = 0,
    PositiveX = 1,
    NegativeZ = 2,
    PositiveZ = 3
}

public readonly record struct TerrainPatchWorldBounds(
    WorldPosition Min,
    WorldPosition Max)
{
    public bool IsValid =>
        Min.IsFinite &&
        Max.IsFinite &&
        Min.X < Max.X &&
        Min.Y <= Max.Y &&
        Min.Z < Max.Z;
}

public readonly struct TerrainPatchRecord
{
    internal TerrainPatchRecord(
        Guid terrainRootGuid,
        Guid tileGuid,
        TerrainTileCoordinate tileCoordinate,
        ulong tileGeneration,
        TerrainPatchKey patchKey,
        int lodLevel,
        int sampleStep,
        int sampleX,
        int sampleZ,
        int sampleIntervalCount,
        TerrainPatchStitchMask stitchMask,
        TerrainTileFlags tileFlags,
        double geometricError,
        double screenSpaceError,
        in TerrainPatchWorldBounds worldBounds,
        in Vector3 originRelativeMin,
        in Vector3 originRelativeMax)
    {
        TerrainRootGuid = terrainRootGuid;
        TileGuid = tileGuid;
        TileCoordinate = tileCoordinate;
        TileGeneration = tileGeneration;
        PatchKey = patchKey;
        LodLevel = lodLevel;
        SampleStep = sampleStep;
        SampleX = sampleX;
        SampleZ = sampleZ;
        SampleIntervalCount = sampleIntervalCount;
        StitchMask = stitchMask;
        TileFlags = tileFlags;
        GeometricError = geometricError;
        ScreenSpaceError = screenSpaceError;
        WorldBounds = worldBounds;
        OriginRelativeMin = originRelativeMin;
        OriginRelativeMax = originRelativeMax;
    }

    public Guid TerrainRootGuid { get; }
    public Guid TileGuid { get; }
    public TerrainTileCoordinate TileCoordinate { get; }
    public ulong TileGeneration { get; }
    public TerrainPatchKey PatchKey { get; }
    public int LodLevel { get; }
    public int SampleStep { get; }
    public int SampleX { get; }
    public int SampleZ { get; }
    public int SampleIntervalCount { get; }
    public TerrainPatchStitchMask StitchMask { get; }
    public TerrainTileFlags TileFlags { get; }
    public double GeometricError { get; }
    public double ScreenSpaceError { get; }
    public TerrainPatchWorldBounds WorldBounds { get; }
    public Vector3 OriginRelativeMin { get; }
    public Vector3 OriginRelativeMax { get; }
}

public static class TerrainPatchTopology
{
    public const int MaximumPatchIntervalCount = 16;

    public static int GetEffectiveEdgeSampleStep(
        in TerrainPatchRecord patch,
        TerrainPatchEdge edge)
    {
        TerrainPatchStitchMask mask = edge switch
        {
            TerrainPatchEdge.NegativeX => TerrainPatchStitchMask.NegativeX,
            TerrainPatchEdge.PositiveX => TerrainPatchStitchMask.PositiveX,
            TerrainPatchEdge.NegativeZ => TerrainPatchStitchMask.NegativeZ,
            TerrainPatchEdge.PositiveZ => TerrainPatchStitchMask.PositiveZ,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
        return (patch.StitchMask & mask) != 0
            ? checked(patch.SampleStep * 2)
            : patch.SampleStep;
    }
}

public readonly record struct TerrainLodMetrics(
    int SourceTileCount,
    int ResidentTileCount,
    int CandidatePatchCount,
    int SelectedPatchCount,
    int CulledPatchCount,
    int UnavailableTileCount,
    int OverflowPatchCount,
    int NeighborRefinementCount,
    int MaxSelectedLod)
{
    public bool Overflowed => OverflowPatchCount > 0;
}

public interface ITerrainLodPlanner
{
    ReadOnlySpan<TerrainPatchRecord> Plan(
        ReadOnlySpan<TerrainTileComponent> visibleTiles,
        in TerrainLodView view,
        in TerrainLodSettings settings);

    TerrainLodMetrics Metrics { get; }
}

internal sealed class TerrainLodPlanner : ITerrainLodPlanner
{
    private readonly TerrainRuntimeDataStore m_RuntimeData;
    private readonly Dictionary<TerrainPatchLocation, int> m_CandidateIndices = new();
    private TerrainPatchCandidate[] m_Candidates = Array.Empty<TerrainPatchCandidate>();
    private TerrainPatchPriority[] m_Priorities = Array.Empty<TerrainPatchPriority>();
    private TerrainPatchHistory[] m_History = Array.Empty<TerrainPatchHistory>();
    private TerrainPatchRecord[] m_Output = Array.Empty<TerrainPatchRecord>();
    private int m_CandidateCount;
    private int m_HistoryCount;
    private int m_OutputCount;

    public TerrainLodPlanner(TerrainRuntimeDataStore runtimeData)
    {
        m_RuntimeData = runtimeData ?? throw new ArgumentNullException(nameof(runtimeData));
    }

    public TerrainLodMetrics Metrics { get; private set; }

    public ReadOnlySpan<TerrainPatchRecord> Plan(
        ReadOnlySpan<TerrainTileComponent> visibleTiles,
        in TerrainLodView view,
        in TerrainLodSettings settings)
    {
        m_CandidateCount = 0;
        m_OutputCount = 0;
        m_CandidateIndices.Clear();
        if (!view.IsValid || !settings.IsValid)
        {
            Metrics = new TerrainLodMetrics(
                visibleTiles.Length, 0, 0, 0, 0, visibleTiles.Length, 0, 0, 0);
            return ReadOnlySpan<TerrainPatchRecord>.Empty;
        }

        int residentTileCount = 0;
        int unavailableTileCount = 0;
        int culledPatchCount = 0;
        for (int tileIndex = 0; tileIndex < visibleTiles.Length; tileIndex++)
        {
            ref readonly TerrainTileComponent component = ref visibleTiles[tileIndex];
            if (!component.IsVisible ||
                !m_RuntimeData.TryGetTile(component.TileGuid, out TerrainResidentTileData resident) ||
                !Matches(component, resident.Tile))
            {
                unavailableTileCount++;
                continue;
            }

            residentTileCount++;
            TerrainTileAcceleration acceleration = resident.Acceleration;
            for (int patchIndex = 0; patchIndex < acceleration.PatchCount; patchIndex++)
            {
                ref readonly TerrainPatchAcceleration patch =
                    ref acceleration.GetPatch(patchIndex);
                TerrainPatchWorldBounds worldBounds = CreateWorldBounds(resident.Tile, patch);
                if (!TryToOriginRelative(
                        worldBounds,
                        view.RenderOrigin,
                        out Vector3 relativeMin,
                        out Vector3 relativeMax))
                {
                    culledPatchCount++;
                    continue;
                }

                if (settings.EnableFrustumCulling &&
                    !TerrainPatchFrustum.IsVisible(
                        relativeMin,
                        relativeMax,
                        view.OriginRelativeViewProjection))
                {
                    culledPatchCount++;
                    continue;
                }

                EnsureCandidateCapacity(m_CandidateCount + 1);
                var identity = new TerrainPatchIdentity(
                    resident.Tile.RootGuid,
                    resident.Tile.Guid,
                    resident.Tile.Coordinate,
                    resident.Generation,
                    new TerrainPatchKey(patch.PatchX, patch.PatchZ));
                double distanceSquared = DistanceSquared(
                    view.CameraWorldPosition,
                    worldBounds);
                int previousLod = FindPreviousLod(identity);
                int lodLevel = SelectLod(
                    acceleration,
                    patchIndex,
                    previousLod,
                    distanceSquared,
                    view,
                    settings);
                m_Candidates[m_CandidateCount++] = new TerrainPatchCandidate(
                    identity,
                    resident,
                    patchIndex,
                    worldBounds,
                    relativeMin,
                    relativeMax,
                    distanceSquared,
                    lodLevel,
                    component.Flags);
            }
        }

        if (m_CandidateCount == 0)
        {
            m_HistoryCount = 0;
            Metrics = new TerrainLodMetrics(
                visibleTiles.Length,
                residentTileCount,
                0,
                0,
                culledPatchCount,
                unavailableTileCount,
                0,
                0,
                0);
            return ReadOnlySpan<TerrainPatchRecord>.Empty;
        }

        SortCandidates(m_Candidates, m_CandidateCount);
        CompactDuplicateCandidates();
        BuildCandidateIndex();
        int neighborRefinements = EnforceNeighborDelta();
        SaveHistory();
        int overflow = SelectBudgetedCandidates(settings.MaximumPatchCount);
        BuildOutput(view);

        int maxSelectedLod = 0;
        for (int index = 0; index < m_OutputCount; index++)
        {
            maxSelectedLod = Math.Max(maxSelectedLod, m_Output[index].LodLevel);
        }

        Metrics = new TerrainLodMetrics(
            visibleTiles.Length,
            residentTileCount,
            m_CandidateCount,
            m_OutputCount,
            culledPatchCount,
            unavailableTileCount,
            overflow,
            neighborRefinements,
            maxSelectedLod);
        return new ReadOnlySpan<TerrainPatchRecord>(m_Output, 0, m_OutputCount);
    }

    internal void Reset()
    {
        m_CandidateCount = 0;
        m_HistoryCount = 0;
        m_OutputCount = 0;
        m_CandidateIndices.Clear();
        Metrics = default;
    }

    private static bool Matches(
        in TerrainTileComponent component,
        Assets.CookedTerrainTile tile) =>
        component.TerrainRootGuid == tile.RootGuid &&
        component.TileGuid == tile.Guid &&
        component.LayerSetGuid == tile.LayerSetGuid &&
        component.TileX == tile.Coordinate.X &&
        component.TileZ == tile.Coordinate.Z &&
        component.WorldPlacement == tile.WorldPlacement;

    private int SelectLod(
        TerrainTileAcceleration acceleration,
        int patchIndex,
        int previousLod,
        double distanceSquared,
        in TerrainLodView view,
        in TerrainLodSettings settings)
    {
        int maximumLod = acceleration.LodLevelCount - 1;
        if (previousLod < 0 || previousLod > maximumLod)
        {
            int selected = 0;
            while (selected < maximumLod &&
                   CalculateScreenError(
                       acceleration.GetGeometricError(patchIndex, selected + 1),
                       distanceSquared,
                       view) <= settings.MaximumScreenSpaceError)
            {
                selected++;
            }

            return selected;
        }

        int lod = previousLod;
        double refineThreshold = settings.MaximumScreenSpaceError *
                                 (1.0 + settings.HysteresisFraction);
        double coarsenThreshold = settings.MaximumScreenSpaceError *
                                  (1.0 - settings.HysteresisFraction);
        while (lod > 0 &&
               CalculateScreenError(
                   acceleration.GetGeometricError(patchIndex, lod),
                   distanceSquared,
                   view) > refineThreshold)
        {
            lod--;
        }

        while (lod < maximumLod &&
               CalculateScreenError(
                   acceleration.GetGeometricError(patchIndex, lod + 1),
                   distanceSquared,
                   view) < coarsenThreshold)
        {
            lod++;
        }

        return lod;
    }

    private static double CalculateScreenError(
        double geometricError,
        double distanceSquared,
        in TerrainLodView view)
    {
        double projected = geometricError * view.ProjectionScale;
        return view.Projection == TerrainLodProjection.Perspective
            ? projected / Math.Max(1.0e-6, Math.Sqrt(distanceSquared))
            : projected;
    }

    private int FindPreviousLod(in TerrainPatchIdentity identity)
    {
        int low = 0;
        int high = m_HistoryCount - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = m_History[middle].Identity.CompareTo(identity);
            if (comparison == 0)
            {
                return m_History[middle].LodLevel;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    private void CompactDuplicateCandidates()
    {
        int writeIndex = 1;
        for (int readIndex = 1; readIndex < m_CandidateCount; readIndex++)
        {
            if (m_Candidates[readIndex].Identity ==
                m_Candidates[writeIndex - 1].Identity)
            {
                continue;
            }

            m_Candidates[writeIndex++] = m_Candidates[readIndex];
        }

        m_CandidateCount = writeIndex;
    }

    private void BuildCandidateIndex()
    {
        m_CandidateIndices.Clear();
        for (int index = 0; index < m_CandidateCount; index++)
        {
            ref readonly TerrainPatchCandidate candidate = ref m_Candidates[index];
            m_CandidateIndices.Add(candidate.Location, index);
        }
    }

    private int EnforceNeighborDelta()
    {
        int refinements = 0;
        bool changed;
        do
        {
            changed = false;
            for (int index = 0; index < m_CandidateCount; index++)
            {
                for (TerrainPatchEdge edge = TerrainPatchEdge.NegativeX;
                     edge <= TerrainPatchEdge.PositiveZ;
                     edge++)
                {
                    if (!TryGetNeighborIndex(index, edge, out int neighborIndex))
                    {
                        continue;
                    }

                    int maximumLod = m_Candidates[neighborIndex].LodLevel + 1;
                    if (m_Candidates[index].LodLevel > maximumLod)
                    {
                        m_Candidates[index].LodLevel = maximumLod;
                        refinements++;
                        changed = true;
                    }
                }
            }
        } while (changed);

        return refinements;
    }

    private int SelectBudgetedCandidates(int maximumPatchCount)
    {
        for (int index = 0; index < m_CandidateCount; index++)
        {
            m_Candidates[index].Selected = false;
        }

        if (m_CandidateCount <= maximumPatchCount)
        {
            for (int index = 0; index < m_CandidateCount; index++)
            {
                m_Candidates[index].Selected = true;
            }

            return 0;
        }

        EnsurePriorityCapacity(m_CandidateCount);
        for (int index = 0; index < m_CandidateCount; index++)
        {
            m_Priorities[index] = new TerrainPatchPriority(
                index,
                m_Candidates[index].DistanceSquared,
                m_Candidates[index].Identity);
        }

        SortPriorities(m_Priorities, m_CandidateCount);
        for (int index = 0; index < maximumPatchCount; index++)
        {
            m_Candidates[m_Priorities[index].CandidateIndex].Selected = true;
        }

        return m_CandidateCount - maximumPatchCount;
    }

    private void BuildOutput(in TerrainLodView view)
    {
        EnsureOutputCapacity(m_CandidateCount);
        int outputCount = 0;
        for (int index = 0; index < m_CandidateCount; index++)
        {
            ref readonly TerrainPatchCandidate candidate = ref m_Candidates[index];
            if (!candidate.Selected)
            {
                continue;
            }

            TerrainPatchStitchMask stitchMask = GetStitchMask(index);
            TerrainTileAcceleration acceleration = candidate.Tile.Acceleration;
            ref readonly TerrainPatchAcceleration patch =
                ref acceleration.GetPatch(candidate.PatchIndex);
            int lodLevel = candidate.LodLevel;
            double geometricError = acceleration.GetGeometricError(
                candidate.PatchIndex,
                lodLevel);
            m_Output[outputCount++] = new TerrainPatchRecord(
                candidate.Identity.RootGuid,
                candidate.Identity.TileGuid,
                candidate.Identity.TileCoordinate,
                candidate.Identity.Generation,
                candidate.Identity.PatchKey,
                lodLevel,
                1 << lodLevel,
                patch.SampleX,
                patch.SampleZ,
                patch.IntervalCount,
                stitchMask,
                candidate.TileFlags,
                geometricError,
                CalculateScreenError(geometricError, candidate.DistanceSquared, view),
                candidate.WorldBounds,
                candidate.RelativeMin,
                candidate.RelativeMax);
        }

        m_OutputCount = outputCount;
    }

    private TerrainPatchStitchMask GetStitchMask(int candidateIndex)
    {
        TerrainPatchStitchMask result = TerrainPatchStitchMask.None;
        ref readonly TerrainPatchCandidate candidate = ref m_Candidates[candidateIndex];
        for (TerrainPatchEdge edge = TerrainPatchEdge.NegativeX;
             edge <= TerrainPatchEdge.PositiveZ;
             edge++)
        {
            if (!TryGetNeighborIndex(candidateIndex, edge, out int neighborIndex) ||
                !m_Candidates[neighborIndex].Selected ||
                candidate.LodLevel >= m_Candidates[neighborIndex].LodLevel)
            {
                continue;
            }

            result |= edge switch
            {
                TerrainPatchEdge.NegativeX => TerrainPatchStitchMask.NegativeX,
                TerrainPatchEdge.PositiveX => TerrainPatchStitchMask.PositiveX,
                TerrainPatchEdge.NegativeZ => TerrainPatchStitchMask.NegativeZ,
                TerrainPatchEdge.PositiveZ => TerrainPatchStitchMask.PositiveZ,
                _ => TerrainPatchStitchMask.None
            };
        }

        return result;
    }

    private bool TryGetNeighborIndex(
        int candidateIndex,
        TerrainPatchEdge edge,
        out int neighborIndex)
    {
        ref readonly TerrainPatchCandidate candidate = ref m_Candidates[candidateIndex];
        int tileX = candidate.Identity.TileCoordinate.X;
        int tileZ = candidate.Identity.TileCoordinate.Z;
        int patchX = candidate.Identity.PatchKey.X;
        int patchZ = candidate.Identity.PatchKey.Z;
        TerrainTileAcceleration acceleration = candidate.Tile.Acceleration;
        switch (edge)
        {
            case TerrainPatchEdge.NegativeX:
                if (patchX > 0) patchX--;
                else
                {
                    tileX--;
                    patchX = acceleration.PatchCountX - 1;
                }
                break;
            case TerrainPatchEdge.PositiveX:
                if (patchX + 1 < acceleration.PatchCountX) patchX++;
                else
                {
                    tileX++;
                    patchX = 0;
                }
                break;
            case TerrainPatchEdge.NegativeZ:
                if (patchZ > 0) patchZ--;
                else
                {
                    tileZ--;
                    patchZ = acceleration.PatchCountZ - 1;
                }
                break;
            case TerrainPatchEdge.PositiveZ:
                if (patchZ + 1 < acceleration.PatchCountZ) patchZ++;
                else
                {
                    tileZ++;
                    patchZ = 0;
                }
                break;
            default:
                neighborIndex = -1;
                return false;
        }

        return m_CandidateIndices.TryGetValue(
            new TerrainPatchLocation(
                candidate.Identity.RootGuid,
                new TerrainTileCoordinate(tileX, tileZ),
                new TerrainPatchKey(patchX, patchZ)),
            out neighborIndex);
    }

    private void SaveHistory()
    {
        EnsureHistoryCapacity(m_CandidateCount);
        for (int index = 0; index < m_CandidateCount; index++)
        {
            m_History[index] = new TerrainPatchHistory(
                m_Candidates[index].Identity,
                m_Candidates[index].LodLevel);
        }

        m_HistoryCount = m_CandidateCount;
    }

    private static TerrainPatchWorldBounds CreateWorldBounds(
        Assets.CookedTerrainTile tile,
        in TerrainPatchAcceleration patch)
    {
        var minimum = new WorldPosition(
            tile.WorldPlacement.X + (patch.SampleX * tile.SampleSpacing.X),
            tile.WorldPlacement.Y + patch.MinHeight,
            tile.WorldPlacement.Z + (patch.SampleZ * tile.SampleSpacing.Z));
        var maximum = new WorldPosition(
            tile.WorldPlacement.X +
            ((patch.SampleX + patch.IntervalCount) * tile.SampleSpacing.X),
            tile.WorldPlacement.Y + patch.MaxHeight,
            tile.WorldPlacement.Z +
            ((patch.SampleZ + patch.IntervalCount) * tile.SampleSpacing.Z));
        var bounds = new TerrainPatchWorldBounds(minimum, maximum);
        if (!bounds.IsValid)
        {
            throw new InvalidOperationException(
                $"Terrain tile '{tile.Guid:D}' patch ({patch.PatchX}, {patch.PatchZ}) " +
                "has invalid world bounds.");
        }

        return bounds;
    }

    private static bool TryToOriginRelative(
        in TerrainPatchWorldBounds bounds,
        in WorldPosition renderOrigin,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = default;
        maximum = default;
        return TryToFloat(bounds.Min, renderOrigin, out minimum) &&
               TryToFloat(bounds.Max, renderOrigin, out maximum);
    }

    private static bool TryToFloat(
        in WorldPosition world,
        in WorldPosition origin,
        out Vector3 value)
    {
        double x = world.X - origin.X;
        double y = world.Y - origin.Y;
        double z = world.Z - origin.Z;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
            Math.Abs(x) > float.MaxValue ||
            Math.Abs(y) > float.MaxValue ||
            Math.Abs(z) > float.MaxValue)
        {
            value = default;
            return false;
        }

        value = new Vector3((float)x, (float)y, (float)z);
        return true;
    }

    private static double DistanceSquared(
        in WorldPosition point,
        in TerrainPatchWorldBounds bounds)
    {
        double dx = DistanceToRange(point.X, bounds.Min.X, bounds.Max.X);
        double dy = DistanceToRange(point.Y, bounds.Min.Y, bounds.Max.Y);
        double dz = DistanceToRange(point.Z, bounds.Min.Z, bounds.Max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static double DistanceToRange(double value, double minimum, double maximum)
    {
        if (value < minimum) return minimum - value;
        return value > maximum ? value - maximum : 0.0;
    }

    private void EnsureCandidateCapacity(int required)
    {
        if (required > m_Candidates.Length)
        {
            Array.Resize(ref m_Candidates, Grow(m_Candidates.Length, required));
        }
    }

    private void EnsurePriorityCapacity(int required)
    {
        if (required > m_Priorities.Length)
        {
            Array.Resize(ref m_Priorities, Grow(m_Priorities.Length, required));
        }
    }

    private void EnsureHistoryCapacity(int required)
    {
        if (required > m_History.Length)
        {
            Array.Resize(ref m_History, Grow(m_History.Length, required));
        }
    }

    private void EnsureOutputCapacity(int required)
    {
        if (required > m_Output.Length)
        {
            Array.Resize(ref m_Output, Grow(m_Output.Length, required));
        }
    }

    private static int Grow(int current, int required)
    {
        int capacity = Math.Max(4, current);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    private static void SortCandidates(TerrainPatchCandidate[] values, int count)
    {
        for (int root = (count >> 1) - 1; root >= 0; root--)
        {
            SiftCandidateDown(values, root, count);
        }

        for (int end = count - 1; end > 0; end--)
        {
            (values[0], values[end]) = (values[end], values[0]);
            SiftCandidateDown(values, 0, end);
        }
    }

    private static void SiftCandidateDown(
        TerrainPatchCandidate[] values,
        int root,
        int count)
    {
        while (true)
        {
            int child = checked((root * 2) + 1);
            if (child >= count)
            {
                return;
            }

            if (child + 1 < count &&
                values[child].Identity.CompareTo(values[child + 1].Identity) < 0)
            {
                child++;
            }

            if (values[root].Identity.CompareTo(values[child].Identity) >= 0)
            {
                return;
            }

            (values[root], values[child]) = (values[child], values[root]);
            root = child;
        }
    }

    private static void SortPriorities(TerrainPatchPriority[] values, int count)
    {
        for (int root = (count >> 1) - 1; root >= 0; root--)
        {
            SiftPriorityDown(values, root, count);
        }

        for (int end = count - 1; end > 0; end--)
        {
            (values[0], values[end]) = (values[end], values[0]);
            SiftPriorityDown(values, 0, end);
        }
    }

    private static void SiftPriorityDown(
        TerrainPatchPriority[] values,
        int root,
        int count)
    {
        while (true)
        {
            int child = checked((root * 2) + 1);
            if (child >= count)
            {
                return;
            }

            if (child + 1 < count && ComparePriority(values[child], values[child + 1]) < 0)
            {
                child++;
            }

            if (ComparePriority(values[root], values[child]) >= 0)
            {
                return;
            }

            (values[root], values[child]) = (values[child], values[root]);
            root = child;
        }
    }

    private static int ComparePriority(
        in TerrainPatchPriority left,
        in TerrainPatchPriority right)
    {
        int result = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return result != 0 ? result : left.Identity.CompareTo(right.Identity);
    }

    private readonly record struct TerrainPatchLocation(
        Guid RootGuid,
        TerrainTileCoordinate TileCoordinate,
        TerrainPatchKey PatchKey);

    private readonly record struct TerrainPatchHistory(
        TerrainPatchIdentity Identity,
        int LodLevel);

    private readonly record struct TerrainPatchPriority(
        int CandidateIndex,
        double DistanceSquared,
        TerrainPatchIdentity Identity);

    private readonly record struct TerrainPatchIdentity(
        Guid RootGuid,
        Guid TileGuid,
        TerrainTileCoordinate TileCoordinate,
        ulong Generation,
        TerrainPatchKey PatchKey) : IComparable<TerrainPatchIdentity>
    {
        public int CompareTo(TerrainPatchIdentity other)
        {
            int result = RootGuid.CompareTo(other.RootGuid);
            if (result != 0) return result;
            result = TileCoordinate.CompareTo(other.TileCoordinate);
            if (result != 0) return result;
            result = TileGuid.CompareTo(other.TileGuid);
            if (result != 0) return result;
            result = Generation.CompareTo(other.Generation);
            return result != 0 ? result : PatchKey.CompareTo(other.PatchKey);
        }
    }

    private struct TerrainPatchCandidate
    {
        public TerrainPatchCandidate(
            TerrainPatchIdentity identity,
            TerrainResidentTileData tile,
            int patchIndex,
            TerrainPatchWorldBounds worldBounds,
            Vector3 relativeMin,
            Vector3 relativeMax,
            double distanceSquared,
            int lodLevel,
            TerrainTileFlags tileFlags)
        {
            Identity = identity;
            Tile = tile;
            PatchIndex = patchIndex;
            WorldBounds = worldBounds;
            RelativeMin = relativeMin;
            RelativeMax = relativeMax;
            DistanceSquared = distanceSquared;
            LodLevel = lodLevel;
            TileFlags = tileFlags;
            Selected = false;
        }

        public TerrainPatchIdentity Identity;
        public TerrainResidentTileData Tile;
        public int PatchIndex;
        public TerrainPatchWorldBounds WorldBounds;
        public Vector3 RelativeMin;
        public Vector3 RelativeMax;
        public double DistanceSquared;
        public int LodLevel;
        public TerrainTileFlags TileFlags;
        public bool Selected;

        public TerrainPatchLocation Location => new(
            Identity.RootGuid,
            Identity.TileCoordinate,
            Identity.PatchKey);
    }

}

internal static class TerrainPatchFrustum
{
    public static bool IsVisible(
        in Vector3 minimum,
        in Vector3 maximum,
        in Matrix4x4 viewProjection)
    {
        Vector3 center = (minimum + maximum) * 0.5f;
        Vector3 extents = Vector3.Abs((maximum - minimum) * 0.5f);
        return !Outside(CreateLeftPlane(viewProjection), center, extents) &&
               !Outside(CreateRightPlane(viewProjection), center, extents) &&
               !Outside(CreateBottomPlane(viewProjection), center, extents) &&
               !Outside(CreateTopPlane(viewProjection), center, extents) &&
               !Outside(CreateNearPlane(viewProjection), center, extents) &&
               !Outside(CreateFarPlane(viewProjection), center, extents);
    }

    private static bool Outside(Vector4 plane, Vector3 center, Vector3 extents)
    {
        float distance =
            (plane.X * center.X) +
            (plane.Y * center.Y) +
            (plane.Z * center.Z) +
            plane.W;
        float radius =
            (MathF.Abs(plane.X) * extents.X) +
            (MathF.Abs(plane.Y) * extents.Y) +
            (MathF.Abs(plane.Z) * extents.Z);
        return distance + radius < 0.0f;
    }

    private static Vector4 CreateLeftPlane(in Matrix4x4 matrix) => new(
        matrix.M14 + matrix.M11,
        matrix.M24 + matrix.M21,
        matrix.M34 + matrix.M31,
        matrix.M44 + matrix.M41);

    private static Vector4 CreateRightPlane(in Matrix4x4 matrix) => new(
        matrix.M14 - matrix.M11,
        matrix.M24 - matrix.M21,
        matrix.M34 - matrix.M31,
        matrix.M44 - matrix.M41);

    private static Vector4 CreateBottomPlane(in Matrix4x4 matrix) => new(
        matrix.M14 + matrix.M12,
        matrix.M24 + matrix.M22,
        matrix.M34 + matrix.M32,
        matrix.M44 + matrix.M42);

    private static Vector4 CreateTopPlane(in Matrix4x4 matrix) => new(
        matrix.M14 - matrix.M12,
        matrix.M24 - matrix.M22,
        matrix.M34 - matrix.M32,
        matrix.M44 - matrix.M42);

    private static Vector4 CreateNearPlane(in Matrix4x4 matrix) => new(
        matrix.M13,
        matrix.M23,
        matrix.M33,
        matrix.M43);

    private static Vector4 CreateFarPlane(in Matrix4x4 matrix) => new(
        matrix.M14 - matrix.M13,
        matrix.M24 - matrix.M23,
        matrix.M34 - matrix.M33,
        matrix.M44 - matrix.M43);
}
