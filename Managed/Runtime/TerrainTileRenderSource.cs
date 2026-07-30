using ArisenEngine.Core.ECS;

namespace ArisenEngine.Terrain;

public interface ITerrainTileRenderSource
{
    ReadOnlySpan<TerrainTileComponent> ExtractVisibleTiles();
}

public sealed class TerrainTileRenderSource : ITerrainTileRenderSource
{
    private readonly Func<EntityManager> m_EntityManagerProvider;
    private TerrainTileComponent[] m_VisibleTiles = Array.Empty<TerrainTileComponent>();
    private int m_VisibleTileCount;

    public TerrainTileRenderSource(Func<EntityManager> entityManagerProvider)
    {
        m_EntityManagerProvider = entityManagerProvider
            ?? throw new ArgumentNullException(nameof(entityManagerProvider));
    }

    public ReadOnlySpan<TerrainTileComponent> ExtractVisibleTiles()
    {
        EntityManager? entityManager = m_EntityManagerProvider();
        if (entityManager == null || !entityManager.HasPool<TerrainTileComponent>())
        {
            m_VisibleTileCount = 0;
            return ReadOnlySpan<TerrainTileComponent>.Empty;
        }

        ComponentPool<TerrainTileComponent> pool =
            entityManager.GetPool<TerrainTileComponent>();
        EnsureCapacity(pool.Count);

        TerrainTileComponent[] components = pool.GetRawComponentArray();
        int visibleCount = 0;
        for (int index = 0; index < pool.Count; index++)
        {
            TerrainTileComponent component = components[index];
            if (!component.IsVisible)
            {
                continue;
            }

            m_VisibleTiles[visibleCount++] = component;
        }

        if (visibleCount > 1)
        {
            Array.Sort(
                m_VisibleTiles,
                0,
                visibleCount,
                TerrainTileRenderOrderComparer.Instance);
        }

        m_VisibleTileCount = visibleCount;
        return new ReadOnlySpan<TerrainTileComponent>(m_VisibleTiles, 0, visibleCount);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= m_VisibleTiles.Length)
        {
            return;
        }

        int capacity = Math.Max(4, m_VisibleTiles.Length);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        Array.Resize(ref m_VisibleTiles, capacity);
    }

    private sealed class TerrainTileRenderOrderComparer : IComparer<TerrainTileComponent>
    {
        public static TerrainTileRenderOrderComparer Instance { get; } = new();

        public int Compare(TerrainTileComponent left, TerrainTileComponent right)
        {
            int result = left.TerrainRootGuid.CompareTo(right.TerrainRootGuid);
            if (result != 0)
            {
                return result;
            }

            result = left.TileZ.CompareTo(right.TileZ);
            if (result != 0)
            {
                return result;
            }

            result = left.TileX.CompareTo(right.TileX);
            return result != 0 ? result : left.TileGuid.CompareTo(right.TileGuid);
        }
    }
}
