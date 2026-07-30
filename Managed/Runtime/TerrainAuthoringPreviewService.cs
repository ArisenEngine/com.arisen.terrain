using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public enum TerrainAuthoringPreviewState
{
    None = 0,
    Queued = 1,
    WaitingForResidency = 2,
    Applied = 3,
    Failed = 4
}

public readonly record struct TerrainAuthoringPreviewStatus(
    Guid RootGuid,
    ulong Revision,
    TerrainAuthoringPreviewState State,
    bool IsDirty,
    int ChangedTileCount,
    int DirtyHeightSampleCount,
    int DirtyWeightSampleCount,
    string Diagnostic)
{
    public static TerrainAuthoringPreviewStatus None(Guid rootGuid) => new(
        rootGuid,
        0,
        TerrainAuthoringPreviewState.None,
        false,
        0,
        0,
        0,
        string.Empty);
}

public interface ITerrainAuthoringPreviewService
{
    void Enqueue(TerrainAuthoringPreviewRevision revision);

    TerrainAuthoringPreviewRevision[] DrainPending();

    bool RequestReapply(Guid rootGuid);

    bool TryGetLatest(Guid rootGuid, out TerrainAuthoringPreviewRevision revision);

    bool TryGetLatestTile(
        Guid tileGuid,
        out TerrainAuthoringPreviewRevision revision,
        out CookedTerrainTile tile);

    TerrainAuthoringPreviewStatus GetStatus(Guid rootGuid);

    void ReportWaiting(Guid rootGuid, ulong revision, string diagnostic);

    void ReportApplied(Guid rootGuid, ulong revision);

    void ReportFailed(Guid rootGuid, ulong revision, string diagnostic);
}

internal sealed class TerrainAuthoringPreviewService : ITerrainAuthoringPreviewService
{
    private readonly object m_Gate = new();
    private readonly int m_MaximumRoots;
    private readonly Dictionary<Guid, TerrainAuthoringPreviewRevision> m_Latest = new();
    private readonly Dictionary<Guid, TerrainAuthoringPreviewRevision> m_Pending = new();
    private readonly Dictionary<Guid, TerrainAuthoringPreviewStatus> m_Status = new();

    public TerrainAuthoringPreviewService(
        int maximumRoots = TerrainAuthoringLimits.MaximumOpenPreviewRoots)
    {
        if (maximumRoots is < 1 or > TerrainAuthoringLimits.MaximumOpenPreviewRoots)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRoots));
        }

        m_MaximumRoots = maximumRoots;
    }

    public void Enqueue(TerrainAuthoringPreviewRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        lock (m_Gate)
        {
            if (!m_Latest.ContainsKey(revision.RootGuid) && m_Latest.Count >= m_MaximumRoots)
            {
                throw new InvalidOperationException(
                    $"Terrain authoring preview root limit '{m_MaximumRoots}' has been reached.");
            }

            if (m_Latest.TryGetValue(
                    revision.RootGuid,
                    out TerrainAuthoringPreviewRevision? current) &&
                revision.Revision <= current.Revision)
            {
                throw new InvalidOperationException(
                    $"Terrain preview revision {revision.Revision} for root " +
                    $"'{revision.RootGuid:D}' is not newer than {current.Revision}.");
            }

            m_Latest[revision.RootGuid] = revision;
            m_Pending[revision.RootGuid] = revision;
            m_Status[revision.RootGuid] = CreateStatus(
                revision,
                TerrainAuthoringPreviewState.Queued,
                string.Empty);
        }
    }

    public TerrainAuthoringPreviewRevision[] DrainPending()
    {
        lock (m_Gate)
        {
            if (m_Pending.Count == 0)
            {
                return Array.Empty<TerrainAuthoringPreviewRevision>();
            }

            TerrainAuthoringPreviewRevision[] revisions = m_Pending.Values
                .OrderBy(revision => revision.RootGuid)
                .ToArray();
            m_Pending.Clear();
            return revisions;
        }
    }

    public bool RequestReapply(Guid rootGuid)
    {
        if (rootGuid == Guid.Empty)
        {
            return false;
        }

        lock (m_Gate)
        {
            if (!m_Latest.TryGetValue(rootGuid, out TerrainAuthoringPreviewRevision? revision))
            {
                return false;
            }

            m_Pending[rootGuid] = revision;
            m_Status[rootGuid] = CreateStatus(
                revision,
                TerrainAuthoringPreviewState.Queued,
                string.Empty);
            return true;
        }
    }

    public bool TryGetLatest(Guid rootGuid, out TerrainAuthoringPreviewRevision revision)
    {
        lock (m_Gate)
        {
            return m_Latest.TryGetValue(rootGuid, out revision!);
        }
    }

    public bool TryGetLatestTile(
        Guid tileGuid,
        out TerrainAuthoringPreviewRevision revision,
        out CookedTerrainTile tile)
    {
        if (tileGuid == Guid.Empty)
        {
            revision = null!;
            tile = null!;
            return false;
        }

        lock (m_Gate)
        {
            foreach (TerrainAuthoringPreviewRevision candidate in m_Latest.Values)
            {
                if (candidate.TryGetChangedTile(tileGuid, out tile!))
                {
                    revision = candidate;
                    return true;
                }
            }
        }

        revision = null!;
        tile = null!;
        return false;
    }

    public TerrainAuthoringPreviewStatus GetStatus(Guid rootGuid)
    {
        lock (m_Gate)
        {
            return m_Status.TryGetValue(rootGuid, out TerrainAuthoringPreviewStatus status)
                ? status
                : TerrainAuthoringPreviewStatus.None(rootGuid);
        }
    }

    public void ReportWaiting(Guid rootGuid, ulong revision, string diagnostic) =>
        UpdateStatus(
            rootGuid,
            revision,
            TerrainAuthoringPreviewState.WaitingForResidency,
            diagnostic);

    public void ReportApplied(Guid rootGuid, ulong revision) =>
        UpdateStatus(
            rootGuid,
            revision,
            TerrainAuthoringPreviewState.Applied,
            string.Empty);

    public void ReportFailed(Guid rootGuid, ulong revision, string diagnostic) =>
        UpdateStatus(
            rootGuid,
            revision,
            TerrainAuthoringPreviewState.Failed,
            diagnostic);

    internal void Clear()
    {
        lock (m_Gate)
        {
            m_Latest.Clear();
            m_Pending.Clear();
            m_Status.Clear();
        }
    }

    private void UpdateStatus(
        Guid rootGuid,
        ulong revision,
        TerrainAuthoringPreviewState state,
        string diagnostic)
    {
        lock (m_Gate)
        {
            if (!m_Latest.TryGetValue(rootGuid, out TerrainAuthoringPreviewRevision? latest) ||
                latest.Revision != revision)
            {
                return;
            }

            m_Status[rootGuid] = CreateStatus(latest, state, NormalizeDiagnostic(diagnostic));
        }
    }

    private static TerrainAuthoringPreviewStatus CreateStatus(
        TerrainAuthoringPreviewRevision revision,
        TerrainAuthoringPreviewState state,
        string diagnostic) => new(
            revision.RootGuid,
            revision.Revision,
            state,
            revision.IsDirty,
            revision.ChangedTiles.Count,
            revision.DirtyHeightSampleCount,
            revision.DirtyWeightSampleCount,
            diagnostic);

    private static string NormalizeDiagnostic(string? diagnostic)
    {
        string value = diagnostic?.Trim() ?? string.Empty;
        return value.Length <= TerrainAuthoringLimits.MaximumPreviewDiagnosticLength
            ? value
            : value[..TerrainAuthoringLimits.MaximumPreviewDiagnosticLength];
    }
}
