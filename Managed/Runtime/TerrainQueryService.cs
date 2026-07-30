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
        CookedTerrainTile tile = resident.Tile;
        double sampleX = Math.Clamp(
            (worldPosition.X - tile.WorldPlacement.X) / tile.SampleSpacing.X,
            0.0,
            tile.Resolution - 1.0);
        double sampleZ = Math.Clamp(
            (worldPosition.Z - tile.WorldPlacement.Z) / tile.SampleSpacing.Z,
            0.0,
            tile.Resolution - 1.0);
        int x0 = Math.Min(tile.Resolution - 2, (int)Math.Floor(sampleX));
        int z0 = Math.Min(tile.Resolution - 2, (int)Math.Floor(sampleZ));
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        double tx = sampleX - x0;
        double tz = sampleZ - z0;

        double h00 = tile.DecodeHeight(tile.GetHeightSample(x0, z0));
        double h10 = tile.DecodeHeight(tile.GetHeightSample(x1, z0));
        double h01 = tile.DecodeHeight(tile.GetHeightSample(x0, z1));
        double h11 = tile.DecodeHeight(tile.GetHeightSample(x1, z1));
        double lowerHeight = Lerp(h00, h10, tx);
        double upperHeight = Lerp(h01, h11, tx);
        double localHeight = Lerp(lowerHeight, upperHeight, tz);
        double gradientX =
            (Lerp(h10 - h00, h11 - h01, tz)) / tile.SampleSpacing.X;
        double gradientZ =
            (Lerp(h01 - h00, h11 - h10, tx)) / tile.SampleSpacing.Z;
        var normal = Normalize(-gradientX, 1.0, -gradientZ);
        Vector4 layerWeights = SampleWeights(tile, x0, z0, x1, z1, tx, tz);
        var surface = new WorldPosition(
            worldPosition.X,
            tile.WorldPlacement.Y + localHeight,
            worldPosition.Z);
        return TerrainQueryResult.Available(
            resident,
            surface,
            normal,
            layerWeights);
    }

    private static Vector4 SampleWeights(
        CookedTerrainTile tile,
        int x0,
        int z0,
        int x1,
        int z1,
        double tx,
        double tz)
    {
        Span<double> weights = stackalloc double[TerrainCookedFormat.WeightChannelCount];
        double sum = 0.0;
        for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
        {
            double w00 = tile.GetLayerWeight(x0, z0, channel);
            double w10 = tile.GetLayerWeight(x1, z0, channel);
            double w01 = tile.GetLayerWeight(x0, z1, channel);
            double w11 = tile.GetLayerWeight(x1, z1, channel);
            double value = Lerp(Lerp(w00, w10, tx), Lerp(w01, w11, tx), tz);
            weights[channel] = value;
            sum += value;
        }

        if (!double.IsFinite(sum) || sum <= 0.0)
        {
            return new Vector4(1.0f, 0.0f, 0.0f, 0.0f);
        }

        double inverse = 1.0 / sum;
        return new Vector4(
            (float)(weights[0] * inverse),
            (float)(weights[1] * inverse),
            (float)(weights[2] * inverse),
            (float)(weights[3] * inverse));
    }

    private static Vector3 Normalize(double x, double y, double z)
    {
        double lengthSquared = (x * x) + (y * y) + (z * z);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= double.Epsilon)
        {
            return Vector3.UnitY;
        }

        double inverseLength = 1.0 / Math.Sqrt(lengthSquared);
        return new Vector3(
            (float)(x * inverseLength),
            (float)(y * inverseLength),
            (float)(z * inverseLength));
    }

    private static double Lerp(double left, double right, double amount) =>
        left + ((right - left) * amount);
}
