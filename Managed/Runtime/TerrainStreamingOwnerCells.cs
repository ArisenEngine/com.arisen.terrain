using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain;

/// <summary>
/// The world cells that own the resident terrain root's tiles.
/// <para>
/// A terrain root is not limited to one world cell. Every canonical tile is a scene dependency of
/// the cell scene it is authored in, so the residency owners of the root's tiles are the fixture's
/// authoritative list of the cells it has to pin, reload, unpin, and drain. The set is ordered by
/// cell identity so the same root always publishes the same owner-cell list.
/// </para>
/// </summary>
internal sealed class TerrainStreamingOwnerCells
{
    private readonly WorldCellDescriptor[] m_Cells;

    private TerrainStreamingOwnerCells(WorldCellDescriptor[] cells) => m_Cells = cells;

    public int Count => m_Cells.Length;

    public IReadOnlyList<WorldCellDescriptor> Cells => m_Cells;

    public WorldCellId[] Ids() => m_Cells.Select(cell => cell.Id).ToArray();

    public string[] IdStrings() => m_Cells.Select(cell => cell.Id.ToString()).ToArray();

    /// <summary>
    /// Derives the owner-cell set from the tiles the fixture can actually see and resolves every
    /// member against the active world descriptor. A tile whose residency owner the active world
    /// does not declare, or that no owner cell of the set owns, is a failure: the fixture would
    /// otherwise pin, reload, and drain a set that does not describe the resident root.
    /// </summary>
    public static TerrainStreamingOwnerCells? TryCreate(
        WorldDescriptor world,
        IReadOnlyList<TerrainTileDiagnosticSnapshot> tiles,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(tiles);
        WorldCellId[] ownerCellIds = tiles
            .SelectMany(tile => tile.Owners)
            .Where(owner =>
                owner.Kind == RuntimeAssetResidencyOwnerKind.WorldCell &&
                owner.CellId.IsValid)
            .Select(owner => owner.CellId)
            .Distinct()
            .Order()
            .ToArray();
        if (ownerCellIds.Length == 0)
        {
            diagnostic =
                "Terrain-streaming fixture found no world-cell residency owner for the resident " +
                "terrain root.";
            return null;
        }

        var cells = new WorldCellDescriptor[ownerCellIds.Length];
        for (int index = 0; index < ownerCellIds.Length; index++)
        {
            WorldCellDescriptor? cell = world.Cells
                .FirstOrDefault(candidate => candidate.Id == ownerCellIds[index]);
            if (cell == null)
            {
                diagnostic =
                    $"Terrain-streaming fixture found terrain tile owner cell " +
                    $"'{ownerCellIds[index]}' that the active world does not declare.";
                return null;
            }

            cells[index] = cell;
        }

        var ownerCells = new TerrainStreamingOwnerCells(cells);
        for (int index = 0; index < tiles.Count; index++)
        {
            if (ownerCells.IsTileOwnedBySet(tiles[index])) continue;
            diagnostic =
                $"Terrain-streaming fixture found canonical tile '{tiles[index].TileGuid}' " +
                "that none of its owner cells owns.";
            return null;
        }

        diagnostic = string.Empty;
        return ownerCells;
    }

    public bool Contains(WorldCellId cellId) => m_Cells.Any(cell => cell.Id == cellId);

    /// <summary>
    /// A canonical tile belongs to the resident root while one of the world cells that own it is
    /// still holding it. Non-world-cell owners (the persistent scene) never satisfy this.
    /// </summary>
    public bool IsTileOwnedBySet(TerrainTileDiagnosticSnapshot tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        for (int index = 0; index < tile.Owners.Count; index++)
        {
            RuntimeAssetResidencyOwnerId owner = tile.Owners[index];
            if (owner.Kind == RuntimeAssetResidencyOwnerKind.WorldCell && Contains(owner.CellId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The reload check is per owner cell: after every owner cell has been reloaded, each canonical
    /// tile has to be held by at least one of its owner cells under that cell's new generation.
    /// </summary>
    public bool IsTileOwnedByCurrentGeneration(
        TerrainTileDiagnosticSnapshot tile,
        IReadOnlyDictionary<WorldCellId, long> currentGenerations)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(currentGenerations);
        for (int index = 0; index < tile.Owners.Count; index++)
        {
            RuntimeAssetResidencyOwnerId owner = tile.Owners[index];
            if (owner.Kind != RuntimeAssetResidencyOwnerKind.WorldCell) continue;
            if (!Contains(owner.CellId)) continue;
            if (!currentGenerations.TryGetValue(owner.CellId, out long generation)) continue;
            if (owner.Generation == generation) return true;
        }

        return false;
    }
}