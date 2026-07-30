namespace ArisenEngine.Terrain;

internal sealed class TerrainDiagnosticsService :
    ITerrainDiagnostics,
    ITerrainDiagnosticsPublisher,
    ITerrainResidencyDiagnostics
{
    private TerrainDiagnosticsSnapshot m_Snapshot = TerrainDiagnosticsSnapshot.Empty;

    public TerrainDiagnosticsSnapshot GetSnapshot() => Volatile.Read(ref m_Snapshot);

    public TerrainResidencyMetrics GetTerrainMetrics() => GetSnapshot().Residency;

    public IReadOnlyList<TerrainResidencyResourceSnapshot> GetTerrainResources() =>
        GetSnapshot().Resources;

    public void Publish(TerrainDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref m_Snapshot, snapshot);
    }

    public void Clear() => Volatile.Write(
        ref m_Snapshot,
        TerrainDiagnosticsSnapshot.Empty);
}
