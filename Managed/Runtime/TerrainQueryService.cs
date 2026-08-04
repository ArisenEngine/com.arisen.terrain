using System.Numerics;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public enum TerrainQueryStatus
{
    InvalidPosition = 0,
    OutsideTerrain = 1,
    Unavailable = 2,
    Available = 3
}

public readonly struct TerrainQueryResult
{
    private TerrainQueryResult(
        TerrainQueryStatus status,
        Guid terrainRootGuid,
        Guid tileGuid,
        TerrainTileCoordinate coordinate,
        ulong tileGeneration,
        WorldPosition surfacePosition,
        Vector3 normal,
        Vector4 layerWeights)
    {
        Status = status;
        TerrainRootGuid = terrainRootGuid;
        TileGuid = tileGuid;
        Coordinate = coordinate;
        TileGeneration = tileGeneration;
        SurfacePosition = surfacePosition;
        Normal = normal;
        LayerWeights = layerWeights;
    }

    public TerrainQueryStatus Status { get; }

    public Guid TerrainRootGuid { get; }

    public Guid TileGuid { get; }

    public TerrainTileCoordinate Coordinate { get; }

    public ulong TileGeneration { get; }

    public WorldPosition SurfacePosition { get; }

    public Vector3 Normal { get; }

    public Vector4 LayerWeights { get; }

    public bool HasTerrain => Status == TerrainQueryStatus.Available;

    internal static TerrainQueryResult Invalid() => new(
        TerrainQueryStatus.InvalidPosition,
        Guid.Empty,
        Guid.Empty,
        default,
        0,
        default,
        default,
        default);

    internal static TerrainQueryResult Outside() => new(
        TerrainQueryStatus.OutsideTerrain,
        Guid.Empty,
        Guid.Empty,
        default,
        0,
        default,
        default,
        default);

    internal static TerrainQueryResult Unavailable(
        Guid rootGuid,
        in CookedTerrainTileReference tile) => new(
        TerrainQueryStatus.Unavailable,
        rootGuid,
        tile.Guid,
        tile.Coordinate,
        0,
        default,
        default,
        default);

    internal static TerrainQueryResult Available(
        TerrainResidentTileData resident,
        in WorldPosition surfacePosition,
        in Vector3 normal,
        in Vector4 layerWeights) => new(
        TerrainQueryStatus.Available,
        resident.Tile.RootGuid,
        resident.Tile.Guid,
        resident.Tile.Coordinate,
        resident.Generation,
        surfacePosition,
        normal,
        layerWeights);
}

public interface ITerrainQueryService
{
    TerrainQueryResult Query(WorldPosition worldPosition);
}

internal sealed class TerrainQueryService : ITerrainQueryService
{
    private readonly TerrainRuntimeDataStore m_RuntimeData;
    private readonly Func<EntityManager> m_EntityManagerProvider;

    public TerrainQueryService(
        TerrainRuntimeDataStore runtimeData,
        Func<EntityManager> entityManagerProvider)
    {
        m_RuntimeData = runtimeData ?? throw new ArgumentNullException(nameof(runtimeData));
        m_EntityManagerProvider = entityManagerProvider
            ?? throw new ArgumentNullException(nameof(entityManagerProvider));
    }

    public TerrainQueryResult Query(WorldPosition worldPosition)
    {
        if (!worldPosition.IsFinite)
        {
            return TerrainQueryResult.Invalid();
        }

        ReadOnlySpan<TerrainResidentRootData> roots = m_RuntimeData.Roots;
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            TerrainResidentRootData root = roots[rootIndex];
            if (!root.Contains(worldPosition.X, worldPosition.Z) ||
                !root.TryResolveTile(
                    worldPosition.X,
                    worldPosition.Z,
                    out CookedTerrainTileReference tileReference))
            {
                continue;
            }

            if (!m_RuntimeData.TryGetTile(tileReference.Guid, out TerrainResidentTileData resident) ||
                resident.Tile.RootGuid != root.Root.Guid ||
                resident.Tile.Coordinate != tileReference.Coordinate ||
                !IsActive(resident.Tile))
            {
                return TerrainQueryResult.Unavailable(root.Root.Guid, tileReference);
            }

            return Sample(resident, worldPosition);
        }

        return TerrainQueryResult.Outside();
    }

    private bool IsActive(CookedTerrainTile tile)
    {
        EntityManager? entityManager = m_EntityManagerProvider();
        if (entityManager == null || !entityManager.HasPool<TerrainTileComponent>())
        {
            return false;
        }

        ComponentPool<TerrainTileComponent> pool =
            entityManager.GetPool<TerrainTileComponent>();
        TerrainTileComponent[] components = pool.GetRawComponentArray();
        for (int index = 0; index < pool.Count; index++)
        {
            ref readonly TerrainTileComponent component = ref components[index];
            if (component.TerrainRootGuid == tile.RootGuid &&
                component.TileGuid == tile.Guid &&
                component.LayerSetGuid == tile.LayerSetGuid &&
                component.TileX == tile.Coordinate.X &&
                component.TileZ == tile.Coordinate.Z &&
                component.WorldPlacement == tile.WorldPlacement)
            {
                return true;
            }
        }

        return false;
    }

    private static TerrainQueryResult Sample(
        TerrainResidentTileData resident,
        in WorldPosition worldPosition)
    {
        TerrainTileSurfaceSampling.Sample(
            resident.Tile,
            worldPosition,
            out WorldPosition surface,
            out Vector3 normal,
            out Vector4 layerWeights);
        return TerrainQueryResult.Available(
            resident,
            surface,
            normal,
            layerWeights);
    }
}
