using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public sealed record TerrainResidencyMetrics(
    int ResidentRootCount,
    int ResidentTileCount,
    long CpuHeightBytes,
    long CpuWeightBytes,
    long CpuErrorBytes,
    long PreparedHeightBytes,
    long PreparedWeightBytes,
    long PreparedErrorBytes,
    long PreparedLayerBytes,
    int LayerDescriptorCount,
    int PendingDisposalCount,
    long SetupCount,
    double LastSetupMilliseconds,
    long BudgetPressureCount);

public sealed record TerrainResidencyResourceSnapshot(
    RuntimeAssetResidencyKey Key,
    Guid TerrainRootGuid,
    Guid TileGuid,
    TerrainTileCoordinate Coordinate,
    RuntimePreparedAssetState State,
    long CpuCookedBytes,
    long PreparedGpuBytes,
    int PinnedOwnerCount,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners,
    string Diagnostic)
{
    public bool IsTile => TileGuid != Guid.Empty;
}

public enum TerrainSeamDiagnosticState
{
    Boundary = 0,
    NeighborUnavailable = 1,
    Valid = 2,
    Incompatible = 3,
    HeightMismatch = 4
}

public sealed record TerrainNeighborDiagnosticSnapshot(
    Guid ExpectedTileGuid,
    bool IsResident,
    TerrainSeamDiagnosticState State,
    int HeightMismatchCount)
{
    public bool IsViolation =>
        State is TerrainSeamDiagnosticState.Incompatible or
            TerrainSeamDiagnosticState.HeightMismatch;
}

public sealed record TerrainTileNeighborDiagnostics(
    TerrainNeighborDiagnosticSnapshot NegativeX,
    TerrainNeighborDiagnosticSnapshot PositiveX,
    TerrainNeighborDiagnosticSnapshot NegativeZ,
    TerrainNeighborDiagnosticSnapshot PositiveZ)
{
    public int ViolationCount =>
        (NegativeX.IsViolation ? 1 : 0) +
        (PositiveX.IsViolation ? 1 : 0) +
        (NegativeZ.IsViolation ? 1 : 0) +
        (PositiveZ.IsViolation ? 1 : 0);
}

public sealed record TerrainPatchDiagnosticSnapshot(
    TerrainPatchKey PatchKey,
    int LodLevel,
    int SampleStep,
    TerrainPatchStitchMask StitchMask,
    double GeometricError,
    double ScreenSpaceError,
    TerrainPatchWorldBounds WorldBounds);

public sealed record TerrainLayerDiagnosticSnapshot(
    int Index,
    string Id,
    Guid AlbedoGuid,
    Guid NormalGuid,
    Guid OrmGuid);

public sealed record TerrainRootDiagnosticSnapshot(
    Guid RootGuid,
    Guid LayerSetGuid,
    string PackageId,
    string Name,
    int CookedVersion,
    int SourceSchemaVersion,
    ulong Generation,
    TerrainPatchWorldBounds WorldBounds,
    int TileCount,
    int ResidentTileCount,
    long CpuCookedBytes,
    long PreparedGpuBytes,
    RuntimePreparedAssetState ResidencyState,
    bool IsDirty,
    bool IsFailed,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners,
    IReadOnlyList<TerrainLayerDiagnosticSnapshot> Layers,
    string Diagnostic);

public sealed record TerrainTileDiagnosticSnapshot(
    Guid TerrainRootGuid,
    Guid TileGuid,
    Guid LayerSetGuid,
    string PackageId,
    TerrainTileCoordinate Coordinate,
    int CookedVersion,
    int SourceSchemaVersion,
    ulong Generation,
    int Resolution,
    int LayerCount,
    TerrainPatchWorldBounds WorldBounds,
    double MinHeight,
    double MaxHeight,
    double MaximumGeometricError,
    int MinimumSelectedLod,
    int MaximumSelectedLod,
    long CpuHeightBytes,
    long CpuWeightBytes,
    long CpuErrorBytes,
    long PreparedGpuBytes,
    RuntimePreparedAssetState ResidencyState,
    bool IsVisible,
    bool IsDirty,
    bool IsFailed,
    TerrainTileNeighborDiagnostics Neighbors,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners,
    IReadOnlyList<TerrainPatchDiagnosticSnapshot> Patches,
    string Diagnostic)
{
    public int SeamViolationCount => Neighbors.ViolationCount;
}

public sealed record TerrainDiagnosticsSnapshot(
    uint FrameIndex,
    TerrainResidencyMetrics Residency,
    TerrainLodMetrics Lod,
    WorldPosition QueryPosition,
    TerrainQueryResult Query,
    IReadOnlyList<TerrainRootDiagnosticSnapshot> Roots,
    IReadOnlyList<TerrainTileDiagnosticSnapshot> Tiles,
    IReadOnlyList<TerrainResidencyResourceSnapshot> Resources,
    int SeamViolationCount,
    int DroppedRootCount,
    int DroppedTileCount,
    int DroppedPatchCount)
{
    public static TerrainDiagnosticsSnapshot Empty { get; } = new(
        0,
        new TerrainResidencyMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0),
        default,
        default,
        default,
        Array.Empty<TerrainRootDiagnosticSnapshot>(),
        Array.Empty<TerrainTileDiagnosticSnapshot>(),
        Array.Empty<TerrainResidencyResourceSnapshot>(),
        0,
        0,
        0,
        0);
}

public interface ITerrainDiagnostics
{
    TerrainDiagnosticsSnapshot GetSnapshot();
}

public interface ITerrainDiagnosticsPublisher
{
    void Publish(TerrainDiagnosticsSnapshot snapshot);

    void Clear();
}

/// <summary>
/// Compatibility view for systems that only need aggregate residency information.
/// </summary>
public interface ITerrainResidencyDiagnostics : ITerrainDiagnostics
{
    TerrainResidencyMetrics GetTerrainMetrics();

    IReadOnlyList<TerrainResidencyResourceSnapshot> GetTerrainResources();
}

public sealed record TerrainDiagnosticRootInput(
    CookedTerrainRoot Root,
    ulong Generation,
    RuntimePreparedAssetState ResidencyState,
    long CpuCookedBytes,
    long PreparedGpuBytes,
    bool IsDirty,
    string Diagnostic,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners);

public sealed record TerrainDiagnosticTileInput(
    CookedTerrainRoot? Root,
    CookedTerrainTileReference Reference,
    CookedTerrainTile? Tile,
    ulong Generation,
    RuntimePreparedAssetState ResidencyState,
    long CpuHeightBytes,
    long CpuWeightBytes,
    long CpuErrorBytes,
    long PreparedGpuBytes,
    bool IsVisible,
    bool IsDirty,
    string Diagnostic,
    IReadOnlyList<RuntimeAssetResidencyOwnerId> Owners);
