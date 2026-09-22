using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Threading;
using ArisenEngine.Terrain.Assets;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Terrain;

internal sealed class TerrainStreamingSmokeScenarioProvider : IRuntimeSmokeScenarioProvider
{
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IWorldOriginService m_Origin;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly ITerrainTileRenderSource m_RenderSource;
    private readonly ITerrainRuntimeDataStore m_RuntimeData;
    private readonly ITerrainQueryService m_Query;
    private readonly ITerrainDiagnostics m_Diagnostics;

    public TerrainStreamingSmokeScenarioProvider(
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IWorldOriginService origin,
        IAssetDatabase assetDatabase,
        IBackgroundTaskScheduler scheduler,
        ITerrainTileRenderSource renderSource,
        ITerrainRuntimeDataStore runtimeData,
        ITerrainQueryService query,
        ITerrainDiagnostics diagnostics)
    {
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Origin = origin;
        m_AssetDatabase = assetDatabase;
        m_Scheduler = scheduler;
        m_RenderSource = renderSource;
        m_RuntimeData = runtimeData;
        m_Query = query;
        m_Diagnostics = diagnostics;
    }

    public bool TryCreateScenario(
        RuntimeSmokeScenarioContext context,
        out IRuntimeSmokeScenario scenario,
        out string diagnostic)
    {
        if (!string.Equals(context.ModeName, "terrain-streaming", StringComparison.Ordinal))
        {
            scenario = null!;
            diagnostic = $"Terrain does not provide smoke scenario '{context.ModeName}'.";
            return false;
        }

        scenario = new TerrainStreamingSmokeScenario(
            context,
            m_Streaming,
            m_Scenes,
            m_Residency,
            m_Origin,
            m_AssetDatabase,
            m_Scheduler,
            m_RenderSource,
            m_RuntimeData,
            m_Query,
            m_Diagnostics);
        diagnostic = string.Empty;
        return true;
    }
}

internal sealed class TerrainStreamingSmokeScenario : IRuntimeSmokeScenario
{
    private const int SoakCycleCount = 4;
    private const double PositionEpsilon = 0.0001;

    private readonly RuntimeSmokeScenarioContext m_Context;
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly IRuntimeSceneService m_Scenes;
    private readonly IRuntimeAssetResidencyService m_Residency;
    private readonly IWorldOriginService m_Origin;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IBackgroundTaskScheduler m_Scheduler;
    private readonly ITerrainTileRenderSource m_RenderSource;
    private readonly ITerrainRuntimeDataStore m_RuntimeData;
    private readonly ITerrainQueryService m_Query;
    private readonly ITerrainDiagnostics m_Diagnostics;
    private readonly List<TerrainStreamingSmokeCheckpoint> m_Checkpoints = new();
    private readonly List<long> m_RebaseStarts = new();
    private readonly List<long> m_RebaseCompletions = new();
    private readonly TerrainStreamingSmokePeaks m_Peaks = new();
    private WorldDescriptor? m_World;
    private EntityManager? m_EntityManager;
    private TerrainStreamingOwnerCells? m_OwnerCells;
    private WorldPosition m_DiscoverySource;
    private WorldPosition m_RebaseSource;
    private Entity m_CameraEntity;
    private WorldPosition m_OriginalCameraPosition;
    private WorldPosition m_NearCameraPosition;
    private WorldPosition m_BoundaryCameraPosition;
    private WorldPosition m_MirrorCameraPosition;
    private WorldPosition m_FarCameraPosition;
    private WorldPosition m_CurrentCameraPosition;
    private Quaternion m_NearRotation;
    private Quaternion m_BoundaryRotation;
    private Quaternion m_MirrorRotation;
    private Quaternion m_FarRotation;
    private Guid m_RootGuid;
    private int m_ExpectedTileCount;
    private int m_ExpectedLayerCount;
    private long m_InitialRebaseSequence;
    private readonly Dictionary<WorldCellId, long> m_SoakReloadGenerations = new();
    private readonly Dictionary<WorldCellId, long> m_SoakCurrentGenerations = new();
    private readonly HashSet<Guid> m_CoveredTiles = new();
    private string m_PendingCapture = string.Empty;
    private uint m_PendingCaptureFrame;
    private TerrainStreamingSmokeBounds? m_LoadedBounds;
    private TerrainStreamingSmokeBounds? m_SoakBaseline;
    private TerrainStreamingDrainSnapshot m_LastDrainSnapshot;
    private TerrainStreamingSmokeStage m_Stage;
    private TerrainStreamingSmokeStage m_TerminalStage;
    private bool m_DiscoveryAimed;
    private int m_SoakCyclesCompleted;
    private string? m_FailureMessage;
    private bool m_ReadyForShutdown;
    private bool m_Complete;
    private bool m_ShutdownDrained;

    public TerrainStreamingSmokeScenario(
        RuntimeSmokeScenarioContext context,
        IRuntimeWorldStreamingService streaming,
        IRuntimeSceneService scenes,
        IRuntimeAssetResidencyService residency,
        IWorldOriginService origin,
        IAssetDatabase assetDatabase,
        IBackgroundTaskScheduler scheduler,
        ITerrainTileRenderSource renderSource,
        ITerrainRuntimeDataStore runtimeData,
        ITerrainQueryService query,
        ITerrainDiagnostics diagnostics)
    {
        m_Context = context;
        m_Streaming = streaming;
        m_Scenes = scenes;
        m_Residency = residency;
        m_Origin = origin;
        m_AssetDatabase = assetDatabase;
        m_Scheduler = scheduler;
        m_RenderSource = renderSource;
        m_RuntimeData = runtimeData;
        m_Query = query;
        m_Diagnostics = diagnostics;
        OutputPath = string.IsNullOrWhiteSpace(context.OutputPath)
            ? GetDefaultOutputPath(context.WorkspacePath, context.ProfileName)
            : Path.GetFullPath(context.OutputPath);
    }

    public string Name => "terrain-streaming";
    public string OutputPath { get; }
    public bool IsReadyForShutdown => m_ReadyForShutdown;
    public bool IsComplete => m_Complete;
    public bool Succeeded => m_Complete && m_FailureMessage == null && m_ShutdownDrained;
    public string? FailureMessage => m_FailureMessage;

    public void Start(uint initialFrameIndex)
    {
        if (!TryBeginAfterStartupWorldReady())
        {
            m_Stage = TerrainStreamingSmokeStage.AwaitStartupWorld;
        }
    }

    private bool TryBeginAfterStartupWorldReady()
    {
        WorldDescriptor? world = m_Streaming.ActiveWorld;
        EntityManager? entityManager = m_Scenes.ActiveScene?.EntityManager;
        if (world == null || entityManager == null)
        {
            return false;
        }

        m_World = world;
        m_EntityManager = entityManager;
        SelectPath(m_World);
        ConfigureValidationBudgets(m_World);
        CaptureCamera();
        m_Origin.RebaseStarting += OnRebaseStarting;
        m_Origin.Rebased += OnRebased;
        m_Streaming.SetStreamingSource(m_DiscoverySource);
        m_Stage = TerrainStreamingSmokeStage.AwaitDiscovery;
        return true;
    }

    public void BeforeFrame(uint frameIndex)
    {
    }

    public void AfterFrame(uint frameIndex)
    {
        if (m_ReadyForShutdown) return;
        using var zone = Profiler.Zone("TerrainStreamingSmoke.AfterFrame");
        if (m_Stage == TerrainStreamingSmokeStage.AwaitStartupWorld)
        {
            TryBeginAfterStartupWorldReady();
            return;
        }

        UpdatePeaks();
        ValidateHardBudgets();
        if (m_FailureMessage != null) return;

        switch (m_Stage)
        {
            case TerrainStreamingSmokeStage.AwaitDiscovery:
                TryCompleteDiscovery(frameIndex);
                break;
            case TerrainStreamingSmokeStage.AwaitNearCapture:
                if (CaptureCompleted("near", frameIndex))
                {
                    CaptureCheckpoint("near", frameIndex);
                    BeginBoundaryCapture(frameIndex);
                }
                break;
            case TerrainStreamingSmokeStage.AwaitBoundaryCapture:
                if (CaptureCompleted("boundary-mixed-lod", frameIndex))
                {
                    CaptureCheckpoint("boundary-mixed-lod", frameIndex);
                    BeginMirrorCapture(frameIndex);
                }
                break;
            case TerrainStreamingSmokeStage.AwaitMirrorCapture:
                if (CaptureCompleted("mirror-cascade", frameIndex))
                {
                    CaptureCheckpoint("mirror-cascade", frameIndex);
                    BeginFarCapture(frameIndex);
                }
                break;
            case TerrainStreamingSmokeStage.AwaitFarCapture:
                if (CaptureCompleted("far-cascade", frameIndex))
                {
                    CaptureCheckpoint("far-cascade", frameIndex);
                    BeginOriginRebase();
                }
                break;
            case TerrainStreamingSmokeStage.AwaitOriginRebase:
                ObserveOriginRebase(frameIndex);
                break;
            case TerrainStreamingSmokeStage.AwaitPostRebaseCapture:
                if (CaptureCompleted("post-rebase", frameIndex))
                {
                    CaptureCheckpoint("post-rebase", frameIndex);
                    BeginReturnedCapture(frameIndex);
                }
                break;
            case TerrainStreamingSmokeStage.AwaitReturnedCapture:
                if (CaptureCompleted("returned-start", frameIndex))
                {
                    CaptureCheckpoint("returned-start", frameIndex);
                    ValidateCoveredTiles();
                    if (m_FailureMessage != null) return;
                    BeginInitialUnload();
                }
                break;
            case TerrainStreamingSmokeStage.AwaitInitialUnload:
                if (TerrainDrained()) BeginSoakLoad();
                break;
            case TerrainStreamingSmokeStage.AwaitSoakLoad:
                ObserveSoakLoad(frameIndex);
                break;
            case TerrainStreamingSmokeStage.AwaitSoakReload:
                ObserveSoakReload(frameIndex);
                break;
            case TerrainStreamingSmokeStage.AwaitSoakUnload:
                ObserveSoakUnload();
                break;
        }

        Profiler.PlotValue("TerrainStreamingSmoke.Stage", (int)m_Stage);
        Profiler.PlotValue("TerrainStreamingSmoke.SoakCycles", m_SoakCyclesCompleted);
    }

    public void ReportFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Unknown terrain-streaming smoke failure.";
        }
        Time.Unpin();

        m_TerminalStage = m_Stage;
        m_FailureMessage ??= message;
        m_Context.VisualSummaryService?.Seal();
        m_ReadyForShutdown = true;
        m_Stage = TerrainStreamingSmokeStage.ReadyForShutdown;
    }

    public void AfterShutdown()
    {
        Time.Unpin();
        m_Origin.RebaseStarting -= OnRebaseStarting;
        m_Origin.Rebased -= OnRebased;
        try
        {
            TerrainDiagnosticsSnapshot diagnostics = m_Diagnostics.GetSnapshot();
            TerrainRuntimeDataMetrics runtimeData = m_RuntimeData.GetMetrics();
            m_ShutdownDrained =
                m_Streaming.ActiveWorld == null &&
                m_Streaming.GetCells().Count == 0 &&
                m_Scenes.GetSceneInstances().Count == 0 &&
                m_Scheduler.OutstandingTaskCount == 0 &&
                m_AssetDatabase.GetLoadedCookedAssetDiagnostics().Count == 0 &&
                m_Residency.GetResources().Count == 0 &&
                m_RenderSource.ExtractVisibleTiles().Length == 0 &&
                runtimeData.RootCount == 0 &&
                runtimeData.TileCount == 0 &&
                diagnostics.Roots.Count == 0 &&
                diagnostics.Tiles.Count == 0 &&
                diagnostics.Resources.Count == 0 &&
                diagnostics.Residency.PendingDisposalCount == 0;
            if (!m_ShutdownDrained)
            {
                m_FailureMessage ??=
                    "Shutdown left terrain tiles, diagnostics, tasks, cooked handles, " +
                    "residency owners, or prepared resources alive.";
            }

            if (m_SoakCyclesCompleted != SoakCycleCount)
            {
                m_FailureMessage ??=
                    $"Completed {m_SoakCyclesCompleted} terrain soak cycle(s), " +
                    $"expected {SoakCycleCount}.";
            }

            if (m_RebaseStarts.Count != 1 ||
                m_RebaseCompletions.Count != 1 ||
                m_RebaseStarts[0] != m_RebaseCompletions[0])
            {
                m_FailureMessage ??=
                    "Terrain camera path did not complete exactly one balanced origin rebase.";
            }

            RuntimeVisualSummaryCaptureResult[] captures =
                m_Context.VisualSummaryService?.GetCaptureResults().ToArray() ?? [];
            if (captures.Length != 0 &&
                (captures.Length != 6 || captures.Any(capture =>
                    capture.State != RuntimeVisualSummaryCaptureState.Succeeded)))
            {
                m_FailureMessage ??=
                    "Terrain visual validation did not complete all six named captures.";
            }
        }
        catch (Exception ex)
        {
            m_FailureMessage ??= $"Terrain shutdown inspection failed: {ex.Message}";
        }

        m_Complete = true;
        WriteArtifact();
    }

    private void TryCompleteDiscovery(uint frameIndex)
    {
        TerrainDiagnosticsSnapshot snapshot = m_Diagnostics.GetSnapshot();
        // Discovery is a render-readiness gate: it rejects a root whose canonical tiles are not all
        // selected and drawn. The authored rotation points the view along the valley, which culls
        // the canonical tiles behind it, so the fixture moves to its own bounds-aimed near pose as
        // soon as the cooked root bounds are known, and only then requires the complete render
        // snapshot.
        if (!m_DiscoveryAimed)
        {
            TerrainRootDiagnosticSnapshot[] candidateRoots = snapshot.Roots
                .Where(root => root.WorldBounds.IsValid)
                .ToArray();
            if (candidateRoots.Length != 1)
            {
                return;
            }

            BuildCameraPath(candidateRoots[0].WorldBounds);
            SetCamera(m_NearCameraPosition, m_NearRotation);
            m_DiscoveryAimed = true;
            return;
        }

        if (!IsTerrainReady(snapshot))
        {
            return;
        }

        TerrainRootDiagnosticSnapshot[] roots = snapshot.Roots
            .Where(root => root.ResidentTileCount > 0)
            .ToArray();
        if (roots.Length != 1)
        {
            ReportFailure(
                $"Terrain-streaming fixture exposed {roots.Length} resident roots; expected one.");
            return;
        }

        TerrainRootDiagnosticSnapshot root = roots[0];
        TerrainTileDiagnosticSnapshot[] tiles = snapshot.Tiles
            .Where(tile => tile.TerrainRootGuid == root.RootGuid)
            .ToArray();
        if (!HasCompleteRenderSnapshot(snapshot, root, tiles))
        {
            return;
        }

        m_OwnerCells = TerrainStreamingOwnerCells.TryCreate(
            m_World!,
            tiles,
            out string ownerDiagnostic);
        if (m_OwnerCells == null)
        {
            ReportFailure(ownerDiagnostic);
            return;
        }

        if (!TryPinOwnerCells("while discovering the resident terrain root"))
        {
            return;
        }

        m_RootGuid = root.RootGuid;
        m_ExpectedTileCount = root.TileCount;
        m_ExpectedLayerCount = root.Layers.Count;
        m_InitialRebaseSequence = m_Origin.RebaseSequence;
        BuildCameraPath(root.WorldBounds);
        m_Streaming.ClearStreamingSource();
        SetCamera(m_NearCameraPosition, m_NearRotation);
        PinStationaryAnimationClock();
        ScheduleCapture("near", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitNearCapture;
    }

    /// <summary>
    /// The post-rebase capture replays the parked far capture and the return-to-start capture
    /// replays the near capture, and both pairs require a visually identical frame, so the
    /// animation clock is pinned across the whole comparison window. Wind is time-driven and would
    /// otherwise advance between those captures, which would leave a genuine rebase-stability
    /// failure indistinguishable from expected animation progress.
    /// </summary>
    private static void PinStationaryAnimationClock()
    {
        Time.Pin(Time.elapsedTime);
    }

    /// <summary>
    /// Whether the resident root is completely drawn for the view the published plan was computed
    /// with. The snapshot carries that view, so the question stays per view: a tile the view sees
    /// has to select at least one patch, and a tile outside it is only required to be resident,
    /// valid and consistent with the total patch count.
    /// </summary>
    internal static bool HasCompleteRenderSnapshot(
        TerrainDiagnosticsSnapshot snapshot,
        TerrainRootDiagnosticSnapshot root,
        IReadOnlyList<TerrainTileDiagnosticSnapshot> tiles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tiles);
        if (root.TileCount <= 0 ||
            snapshot.Lod.SourceTileCount != root.TileCount ||
            snapshot.Lod.ResidentTileCount != root.TileCount ||
            snapshot.Lod.SelectedPatchCount <= 0 ||
            snapshot.Lod.OverflowPatchCount != 0 ||
            tiles.Count != root.TileCount)
        {
            return false;
        }

        int patchCount = 0;
        for (int index = 0; index < tiles.Count; index++)
        {
            TerrainTileDiagnosticSnapshot tile = tiles[index];
            if (tile.TerrainRootGuid != root.RootGuid ||
                tile.ResidencyState != RuntimePreparedAssetState.Ready ||
                !tile.IsVisible ||
                tile.IsFailed ||
                (tile.Patches.Count == 0 && snapshot.PlanView.IsFrustumVisible(tile.WorldBounds)))
            {
                return false;
            }

            patchCount = checked(patchCount + tile.Patches.Count);
        }

        return patchCount == snapshot.Lod.SelectedPatchCount;
    }

    private void BeginBoundaryCapture(uint frameIndex)
    {
        SetCamera(m_BoundaryCameraPosition, m_BoundaryRotation);
        m_Streaming.SetStreamingSource(m_BoundaryCameraPosition);
        ScheduleCapture("boundary-mixed-lod", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitBoundaryCapture;
    }

    private void BeginFarCapture(uint frameIndex)
    {
        SetCamera(m_FarCameraPosition, m_FarRotation);
        m_Streaming.SetStreamingSource(m_FarCameraPosition);
        ScheduleCapture("far-cascade", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitFarCapture;
    }

    /// <summary>
    /// Captures the fourth corner of the root. The near, boundary and far poses frame three of the
    /// four corners the fixture's path visits; on a root wider than one frustum the corner region of
    /// the fourth is out of sight of all three - a view never frames the ground under its own
    /// corner, and the diagonal corner is beyond the camera's far plane - so the aggregate coverage
    /// check would fail without this capture.
    /// </summary>
    private void BeginMirrorCapture(uint frameIndex)
    {
        SetCamera(m_MirrorCameraPosition, m_MirrorRotation);
        m_Streaming.SetStreamingSource(m_MirrorCameraPosition);
        ScheduleCapture("mirror-cascade", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitMirrorCapture;
    }

    /// <summary>
    /// Rebases the world under the camera that was captured last. The camera stays parked on the far
    /// pose: the streaming source alone decides when <see cref="IWorldOriginService"/> rebases, so
    /// moving the source across the rebase threshold is enough to trigger one, and leaving the camera
    /// where it was is what makes the post-rebase capture comparable to the far capture. A capture
    /// pair that also replayed a camera path would fold the origin change, the terrain residency
    /// churn and the LOD history of that path into the same frame difference, and the fixture could
    /// not tell a rebase regression from the expected change of a mixed-LOD plan.
    /// </summary>
    private void BeginOriginRebase()
    {
        m_Streaming.SetStreamingSource(m_RebaseSource);
        m_Stage = TerrainStreamingSmokeStage.AwaitOriginRebase;
    }

    /// <summary>
    /// Waits for the sourced rebase and captures the parked far pose again. The frame is not
    /// re-staged through <c>SetCamera</c>: the origin service has just shifted every origin-relative
    /// transform, the camera included, so the capture only matches the pre-rebase far capture while
    /// the rebase preserved the parked view.
    /// </summary>
    private void ObserveOriginRebase(uint frameIndex)
    {
        long expectedSequence = checked(m_InitialRebaseSequence + 1);
        if (m_Origin.RebaseSequence < expectedSequence) return;
        if (m_Origin.RebaseSequence != expectedSequence)
        {
            ReportFailure("Terrain camera path triggered more than one origin rebase.");
            return;
        }

        m_Streaming.ClearStreamingSource();
        ScheduleCapture("post-rebase", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitPostRebaseCapture;
    }

    private void BeginReturnedCapture(uint frameIndex)
    {
        SetCamera(m_NearCameraPosition, m_NearRotation);
        ScheduleCapture("returned-start", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitReturnedCapture;
    }

    private void BeginInitialUnload()
    {
        Time.Unpin();
        m_Streaming.ClearStreamingSource();
        if (!TryUnpinOwnerCells("before soak validation")) return;
        m_Stage = TerrainStreamingSmokeStage.AwaitInitialUnload;
    }

    private void BeginSoakLoad()
    {
        if (!TryPinOwnerCells("for soak validation")) return;
        // The reload soak verifies that repeated load/unload cycles return to the loaded steady
        // state the camera path reached. The baseline is frozen here, on the first cycle, so every
        // later cycle is compared against the same loaded world at the same camera pose.
        m_SoakBaseline ??= m_LoadedBounds;
        m_Stage = TerrainStreamingSmokeStage.AwaitSoakLoad;
    }

    private void ObserveSoakLoad(uint frameIndex)
    {
        if (!TerrainCellsReady(out WorldCellStreamingSnapshot[] cells)) return;
        CaptureCheckpoint($"soak-load-{m_SoakCyclesCompleted + 1}", frameIndex);
        if (m_FailureMessage != null) return;

        m_SoakReloadGenerations.Clear();
        foreach (WorldCellStreamingSnapshot cell in cells)
        {
            m_SoakReloadGenerations[cell.CellId] = cell.RequestGeneration;
            if (!m_Streaming.RequestCellReload(cell.CellId))
            {
                ReportFailure(
                    $"Terrain soak could not request a reload of owner cell '{cell.CellId}'.");
                return;
            }
        }

        m_Stage = TerrainStreamingSmokeStage.AwaitSoakReload;
    }

    private void ObserveSoakReload(uint frameIndex)
    {
        if (!TerrainCellsReady(out WorldCellStreamingSnapshot[] cells)) return;
        m_SoakCurrentGenerations.Clear();
        foreach (WorldCellStreamingSnapshot cell in cells)
        {
            if (!m_SoakReloadGenerations.TryGetValue(cell.CellId, out long reloaded) ||
                cell.RequestGeneration <= reloaded)
            {
                return;
            }

            m_SoakCurrentGenerations[cell.CellId] = cell.RequestGeneration;
        }

        TerrainTileDiagnosticSnapshot[] tiles = m_Diagnostics.GetSnapshot().Tiles
            .Where(tile => tile.TerrainRootGuid == m_RootGuid)
            .ToArray();
        bool currentOwnerGeneration =
            tiles.Length == m_ExpectedTileCount &&
            m_OwnerCells != null &&
            tiles.All(tile => m_OwnerCells.IsTileOwnedByCurrentGeneration(
                tile,
                m_SoakCurrentGenerations));
        if (!currentOwnerGeneration) return;

        CaptureCheckpoint($"soak-reload-{m_SoakCyclesCompleted + 1}", frameIndex);
        if (m_FailureMessage != null) return;
        if (!TryUnpinOwnerCells("after the soak reload")) return;
        m_Stage = TerrainStreamingSmokeStage.AwaitSoakUnload;
    }

    private void ObserveSoakUnload()
    {
        if (!TerrainDrained()) return;
        m_SoakCyclesCompleted++;
        if (m_SoakCyclesCompleted < SoakCycleCount)
        {
            BeginSoakLoad();
            return;
        }

        m_Context.VisualSummaryService?.Seal();
        m_ReadyForShutdown = true;
        m_Stage = TerrainStreamingSmokeStage.ReadyForShutdown;
    }

    private TerrainStreamingSmokeCheckpoint CaptureCheckpoint(string name, uint frameIndex)
    {
        TerrainDiagnosticsSnapshot snapshot = m_Diagnostics.GetSnapshot();
        TerrainLodView planView = snapshot.PlanView;
        TerrainTileComponent[] components = m_RenderSource.ExtractVisibleTiles().ToArray();
        TerrainRootDiagnosticSnapshot? root = snapshot.Roots.SingleOrDefault(candidate =>
            candidate.RootGuid == m_RootGuid);
        TerrainTileDiagnosticSnapshot[] tiles = snapshot.Tiles
            .Where(tile => tile.TerrainRootGuid == m_RootGuid)
            .OrderBy(tile => tile.Coordinate.Z)
            .ThenBy(tile => tile.Coordinate.X)
            .ThenBy(tile => tile.TileGuid)
            .ToArray();
        // The coverage contract is per view: this checkpoint requires every tile its own plan view
        // sees to select at least one patch, and only those. A resident tile outside the view
        // legitimately selects nothing once the root is wider than the frustum, and the aggregate
        // check the flight ends with is what keeps every canonical tile accounted for.
        var expectedTiles = new bool[tiles.Length];
        int expectedTileCount = 0;
        bool tilesValid = true;
        for (int index = 0; index < tiles.Length; index++)
        {
            TerrainTileDiagnosticSnapshot tile = tiles[index];
            bool expected = planView.IsFrustumVisible(tile.WorldBounds);
            expectedTiles[index] = expected;
            if (expected) expectedTileCount++;
            tilesValid &= ValidateTile(tile, expected);
        }

        bool valid = root != null &&
            IsTerrainReady(snapshot) &&
            components.Length == m_ExpectedTileCount &&
            components.Select(component => component.TileGuid).Distinct().Count() ==
                m_ExpectedTileCount &&
            components.All(component => component.TerrainRootGuid == m_RootGuid) &&
            tiles.Length == m_ExpectedTileCount &&
            expectedTileCount > 0 &&
            tilesValid &&
            tiles.Sum(tile => tile.Patches.Count) == snapshot.Lod.SelectedPatchCount &&
            snapshot.Lod.SelectedPatchCount > 0 &&
            snapshot.Lod.OverflowPatchCount == 0 &&
            snapshot.DroppedRootCount == 0 &&
            snapshot.DroppedTileCount == 0 &&
            snapshot.DroppedPatchCount == 0;

        var querySamples = new TerrainStreamingQuerySample[tiles.Length];
        for (int index = 0; index < tiles.Length; index++)
        {
            TerrainTileDiagnosticSnapshot tile = tiles[index];
            WorldPosition center = Center(tile.WorldBounds);
            TerrainQueryResult result = m_Query.Query(center);
            bool queryValid = result.Status == TerrainQueryStatus.Available &&
                result.TerrainRootGuid == m_RootGuid &&
                result.TileGuid == tile.TileGuid &&
                result.Coordinate == tile.Coordinate &&
                result.TileGeneration == tile.Generation &&
                result.SurfacePosition.IsFinite &&
                IsFinite(result.Normal) &&
                IsFinite(result.LayerWeights) &&
                Math.Abs(
                    result.LayerWeights.X + result.LayerWeights.Y +
                    result.LayerWeights.Z + result.LayerWeights.W - 1.0f) <= 0.001f;
            valid &= queryValid;
            querySamples[index] = new TerrainStreamingQuerySample(
                tile.TileGuid,
                tile.Coordinate,
                result.Status,
                result.TileGeneration,
                result.SurfacePosition,
                result.Normal,
                result.LayerWeights,
                queryValid);
        }

        TerrainStreamingMemorySnapshot memory = CaptureMemory();
        var checkpoint = new TerrainStreamingSmokeCheckpoint(
            name,
            frameIndex,
            m_CurrentCameraPosition,
            m_Origin.GetSnapshot(),
            m_RootGuid,
            m_OwnerCells?.IdStrings() ?? Array.Empty<string>(),
            tiles.Select((tile, index) => new TerrainStreamingTileSnapshot(
                tile.TileGuid,
                tile.Coordinate,
                tile.Generation,
                tile.MinimumSelectedLod,
                tile.MaximumSelectedLod,
                tile.Patches.Count,
                expectedTiles[index],
                tile.WorldBounds,
                tile.SeamViolationCount,
                TerrainTileOwnerCellIds(tile))).ToArray(),
            expectedTileCount,
            BuildLodHistogram(tiles),
            snapshot.Lod,
            snapshot.SeamViolationCount,
            components.Length,
            querySamples,
            memory,
            valid);
        m_Checkpoints.Add(checkpoint);
        if (!valid)
        {
            ReportFailure(
                $"Terrain checkpoint '{name}' found a tile its own view sees with no selected " +
                "patch, a view that sees no tile at all, or a tile with invalid residency, LOD, " +
                "patch bounds, ECS ownership, query parity, or seam state.");
            return checkpoint;
        }

        for (int index = 0; index < tiles.Length; index++)
        {
            if (expectedTiles[index]) m_CoveredTiles.Add(tiles[index].TileGuid);
        }

        ValidateLoadedBounds(memory);
        return checkpoint;
    }

    /// <summary>
    /// The aggregate half of the coverage contract: between them the captured views have to see
    /// every canonical tile of the resident root. A view cannot frame a root wider than its own
    /// frustum, so without this requirement a regional root would quietly reduce the gate to
    /// whichever part of the world one corner happens to frame.
    /// </summary>
    private void ValidateCoveredTiles()
    {
        if (m_ExpectedTileCount <= 0 || m_CoveredTiles.Count == m_ExpectedTileCount)
        {
            return;
        }

        const int maximumReportedTiles = 8;
        string[] missing = m_Diagnostics.GetSnapshot().Tiles
            .Where(tile => tile.TerrainRootGuid == m_RootGuid && !m_CoveredTiles.Contains(tile.TileGuid))
            .OrderBy(tile => tile.Coordinate.Z)
            .ThenBy(tile => tile.Coordinate.X)
            .Take(maximumReportedTiles)
            .Select(tile => $"({tile.Coordinate.X},{tile.Coordinate.Z})")
            .ToArray();
        ReportFailure(
            $"Terrain camera path covered {m_CoveredTiles.Count} of {m_ExpectedTileCount} " +
            $"canonical tile(s): no captured pose sees {string.Join(", ", missing)}.");
    }

    private bool ValidateTile(TerrainTileDiagnosticSnapshot tile, bool expected)
    {
        if (tile.ResidencyState != RuntimePreparedAssetState.Ready ||
            !tile.IsVisible ||
            tile.IsFailed ||
            tile.SeamViolationCount != 0 ||
            (expected && tile.Patches.Count == 0) ||
            !tile.WorldBounds.IsValid ||
            m_OwnerCells is not { } ownerCells ||
            !ownerCells.IsTileOwnedBySet(tile))
        {
            return false;
        }

        for (int index = 0; index < tile.Patches.Count; index++)
        {
            TerrainPatchDiagnosticSnapshot patch = tile.Patches[index];
            if (!patch.WorldBounds.IsValid ||
                patch.LodLevel < 0 ||
                patch.SampleStep <= 0 ||
                !Contains(tile.WorldBounds, patch.WorldBounds))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every owner cell of the resident root has to reach the loaded state together, and the root
    /// itself has to be completely resident and drawn, before the soak may act on it.
    /// </summary>
    private bool TerrainCellsReady(out WorldCellStreamingSnapshot[] cells)
    {
        cells = Array.Empty<WorldCellStreamingSnapshot>();
        TerrainStreamingOwnerCells? ownerCells = m_OwnerCells;
        if (ownerCells == null) return false;
        IReadOnlyList<WorldCellStreamingSnapshot> streaming = m_Streaming.GetCells();
        var ready = new WorldCellStreamingSnapshot[ownerCells.Count];
        for (int index = 0; index < ready.Length; index++)
        {
            WorldCellStreamingSnapshot? cell = FindStreamingCell(
                streaming,
                ownerCells.Cells[index].Id);
            if (cell == null ||
                cell.State != WorldCellStreamingState.Active ||
                !cell.Pinned)
            {
                return false;
            }

            ready[index] = cell;
        }

        TerrainDiagnosticsSnapshot snapshot = m_Diagnostics.GetSnapshot();
        bool rootReady =
            IsTerrainReady(snapshot) &&
            snapshot.Lod.SourceTileCount == m_ExpectedTileCount &&
            snapshot.Lod.ResidentTileCount == m_ExpectedTileCount &&
            snapshot.Lod.SelectedPatchCount > 0 &&
            snapshot.Tiles.Count(tile => tile.TerrainRootGuid == m_RootGuid) ==
                m_ExpectedTileCount &&
            snapshot.Tiles
                .Where(tile => tile.TerrainRootGuid == m_RootGuid)
                .All(tile => tile.IsVisible && tile.Patches.Count > 0) &&
            m_RenderSource.ExtractVisibleTiles().Length == m_ExpectedTileCount;
        if (!rootReady) return false;
        cells = ready;
        return true;
    }

    private static WorldCellStreamingSnapshot? FindStreamingCell(
        IReadOnlyList<WorldCellStreamingSnapshot> cells,
        WorldCellId cellId)
    {
        for (int index = 0; index < cells.Count; index++)
        {
            if (cells[index].CellId == cellId) return cells[index];
        }

        return null;
    }

    private bool TryPinOwnerCells(string purpose)
    {
        TerrainStreamingOwnerCells? ownerCells = m_OwnerCells;
        if (ownerCells == null)
        {
            ReportFailure($"Terrain-streaming fixture has no owner cells to pin {purpose}.");
            return false;
        }

        int pinned = 0;
        foreach (WorldCellDescriptor cell in ownerCells.Cells)
        {
            if (m_Streaming.PinCell(cell.Id))
            {
                pinned++;
                continue;
            }

            ReportFailure(
                $"Could not pin terrain owner cell '{cell.Id}' {purpose} " +
                $"({pinned} of {ownerCells.Count} pinned).");
            return false;
        }

        return true;
    }

    private bool TryUnpinOwnerCells(string purpose)
    {
        TerrainStreamingOwnerCells? ownerCells = m_OwnerCells;
        if (ownerCells == null)
        {
            ReportFailure($"Terrain-streaming fixture has no owner cells to unpin {purpose}.");
            return false;
        }

        int unpinned = 0;
        foreach (WorldCellDescriptor cell in ownerCells.Cells)
        {
            if (m_Streaming.UnpinCell(cell.Id))
            {
                unpinned++;
                continue;
            }

            ReportFailure(
                $"Could not unpin terrain owner cell '{cell.Id}' {purpose} " +
                $"({unpinned} of {ownerCells.Count} unpinned).");
            return false;
        }

        return true;
    }

    private bool TerrainDrained()
    {
        TerrainStreamingOwnerCells? ownerCells = m_OwnerCells;
        if (ownerCells == null) return false;
        IReadOnlyList<WorldCellStreamingSnapshot> streaming = m_Streaming.GetCells();
        var cells = new TerrainStreamingCellDrainSnapshot[ownerCells.Count];
        for (int index = 0; index < cells.Length; index++)
        {
            WorldCellId cellId = ownerCells.Cells[index].Id;
            WorldCellStreamingSnapshot? cell = FindStreamingCell(streaming, cellId);
            cells[index] = cell == null
                ? TerrainStreamingCellDrainSnapshot.Untracked(cellId)
                : new TerrainStreamingCellDrainSnapshot(
                    cellId.ToString(),
                    Tracked: true,
                    cell.State,
                    cell.Desired,
                    cell.DesiredSources,
                    cell.Pinned);
        }

        TerrainDiagnosticsSnapshot diagnostics = m_Diagnostics.GetSnapshot();
        TerrainRuntimeDataMetrics runtimeData = m_RuntimeData.GetMetrics();
        RuntimeAssetResidencySnapshot[] terrainResources = m_Residency.GetResources()
            .Where(resource => resource.Key.AssetType is "TerrainRoot" or "TerrainTile")
            .ToArray();
        m_LastDrainSnapshot = new TerrainStreamingDrainSnapshot(
            cells,
            m_RenderSource.ExtractVisibleTiles().Length,
            runtimeData.RootCount,
            runtimeData.TileCount,
            diagnostics.Roots.Count,
            diagnostics.Tiles.Count,
            diagnostics.Resources.Count,
            terrainResources.Length,
            diagnostics.Residency.PendingDisposalCount,
            m_Residency.GetMetrics().PendingDisposalCount,
            m_Scheduler.OutstandingTaskCount);
        return m_LastDrainSnapshot.IsDrained;
    }

    private bool IsTerrainReady(TerrainDiagnosticsSnapshot snapshot)
    {
        if (snapshot.SeamViolationCount != 0 ||
            snapshot.Residency.PendingDisposalCount != 0 ||
            snapshot.Roots.Count == 0 ||
            snapshot.Tiles.Count == 0)
        {
            return false;
        }

        TerrainRootDiagnosticSnapshot[] roots = m_RootGuid == Guid.Empty
            ? snapshot.Roots.Where(root => root.ResidentTileCount > 0).ToArray()
            : snapshot.Roots.Where(root => root.RootGuid == m_RootGuid).ToArray();
        if (roots.Length != 1) return false;
        TerrainRootDiagnosticSnapshot root = roots[0];
        int expectedTiles = m_ExpectedTileCount == 0 ? root.TileCount : m_ExpectedTileCount;
        TerrainTileDiagnosticSnapshot[] tiles = snapshot.Tiles
            .Where(tile => tile.TerrainRootGuid == root.RootGuid)
            .ToArray();
        return expectedTiles > 0 &&
            root.ResidencyState == RuntimePreparedAssetState.Ready &&
            root.ResidentTileCount == expectedTiles &&
            !root.IsFailed &&
            tiles.Length == expectedTiles &&
            tiles.All(tile =>
                tile.ResidencyState == RuntimePreparedAssetState.Ready &&
                !tile.IsFailed &&
                tile.SeamViolationCount == 0);
    }

    private void ValidateLoadedBounds(TerrainStreamingMemorySnapshot memory)
    {
        var current = new TerrainStreamingSmokeBounds(
            memory.AllocatedEntitySlots,
            memory.LoadedCookedHandles,
            memory.ResidentAssets,
            memory.PreparedDescriptors,
            memory.TerrainCpuBytes,
            memory.TerrainPreparedBytes,
            memory.TerrainLayerDescriptors,
            memory.SelectedPatches);
        if (m_SoakBaseline == null)
        {
            // Every named checkpoint frames a different amount of the world, so the loaded steady
            // state the soak compares against is the running high-water mark of the camera path.
            // Comparing a later pose against the first checkpoint would report the camera path's
            // own visibility changes as reload growth.
            m_LoadedBounds = m_LoadedBounds == null
                ? current
                : HighWaterMark(m_LoadedBounds, current);
            return;
        }

        if (ExceedsLoadedBounds(current, m_SoakBaseline) ||
            current.SelectedPatches > TerrainLodSettings.Default.MaximumPatchCount)
        {
            ReportFailure("Terrain reload soak exceeded its first loaded steady-state bounds.");
        }
    }

    private static TerrainStreamingSmokeBounds HighWaterMark(
        TerrainStreamingSmokeBounds baseline,
        TerrainStreamingSmokeBounds current) => new(
        Math.Max(baseline.AllocatedEntitySlots, current.AllocatedEntitySlots),
        Math.Max(baseline.LoadedCookedHandles, current.LoadedCookedHandles),
        Math.Max(baseline.ResidentAssets, current.ResidentAssets),
        Math.Max(baseline.PreparedDescriptors, current.PreparedDescriptors),
        Math.Max(baseline.TerrainCpuBytes, current.TerrainCpuBytes),
        Math.Max(baseline.TerrainPreparedBytes, current.TerrainPreparedBytes),
        Math.Max(baseline.TerrainLayerDescriptors, current.TerrainLayerDescriptors),
        Math.Max(baseline.SelectedPatches, current.SelectedPatches));

    private static bool ExceedsLoadedBounds(
        TerrainStreamingSmokeBounds current,
        TerrainStreamingSmokeBounds baseline) =>
        current.AllocatedEntitySlots > baseline.AllocatedEntitySlots ||
        current.LoadedCookedHandles > baseline.LoadedCookedHandles ||
        current.ResidentAssets > baseline.ResidentAssets ||
        current.PreparedDescriptors > baseline.PreparedDescriptors ||
        current.TerrainCpuBytes > baseline.TerrainCpuBytes ||
        current.TerrainPreparedBytes > baseline.TerrainPreparedBytes ||
        current.TerrainLayerDescriptors > baseline.TerrainLayerDescriptors;

    private void ValidateHardBudgets()
    {
        WorldStreamingMetrics streaming = m_Streaming.GetMetrics();
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        TerrainDiagnosticsSnapshot terrain = m_Diagnostics.GetSnapshot();
        if (m_World != null &&
            (streaming.ActiveCells > m_World.Partition.MaxActiveCells ||
             streaming.BytesInFlight > m_Streaming.Budgets.MaxBytesInFlight ||
             streaming.DecodedStagingBytes > m_Streaming.Budgets.MaxDecodedStagingBytes ||
             residency.CpuCookedBytes > m_Residency.Budgets.MaxCpuCookedBytes ||
             residency.PreparedGpuBytes > m_Residency.Budgets.MaxPreparedGpuBytes))
        {
            ReportFailure("Terrain-streaming smoke exceeded a configured streaming/residency budget.");
            return;
        }

        if (terrain.Lod.OverflowPatchCount != 0 ||
            terrain.DroppedRootCount != 0 ||
            terrain.DroppedTileCount != 0 ||
            terrain.DroppedPatchCount != 0 ||
            terrain.SeamViolationCount != 0)
        {
            ReportFailure("Terrain diagnostics reported overflow, truncation, or a seam violation.");
            return;
        }

        if (m_ExpectedTileCount > 0)
        {
            int disposalLimit = Math.Max(
                32,
                checked((m_ExpectedTileCount * 8) + (m_ExpectedLayerCount * 6)));
            if (terrain.Residency.ResidentRootCount > 1 ||
                terrain.Residency.ResidentTileCount > m_ExpectedTileCount ||
                terrain.Residency.LayerDescriptorCount > m_ExpectedLayerCount ||
                terrain.Residency.PendingDisposalCount > disposalLimit ||
                terrain.Lod.SelectedPatchCount > TerrainLodSettings.Default.MaximumPatchCount)
            {
                ReportFailure("Terrain resource or patch capacity exceeded its fixture-derived bound.");
            }
        }
    }

    private void UpdatePeaks()
    {
        TerrainStreamingMemorySnapshot current = CaptureMemory();
        m_Peaks.AllocatedEntitySlots = Math.Max(
            m_Peaks.AllocatedEntitySlots,
            current.AllocatedEntitySlots);
        m_Peaks.LoadedCookedHandles = Math.Max(
            m_Peaks.LoadedCookedHandles,
            current.LoadedCookedHandles);
        m_Peaks.LoadedCookedBytes = Math.Max(
            m_Peaks.LoadedCookedBytes,
            current.LoadedCookedBytes);
        m_Peaks.ResidentAssets = Math.Max(m_Peaks.ResidentAssets, current.ResidentAssets);
        m_Peaks.PreparedGpuBytes = Math.Max(
            m_Peaks.PreparedGpuBytes,
            current.PreparedGpuBytes);
        m_Peaks.PreparedDescriptors = Math.Max(
            m_Peaks.PreparedDescriptors,
            current.PreparedDescriptors);
        m_Peaks.TerrainCpuBytes = Math.Max(
            m_Peaks.TerrainCpuBytes,
            current.TerrainCpuBytes);
        m_Peaks.TerrainPreparedBytes = Math.Max(
            m_Peaks.TerrainPreparedBytes,
            current.TerrainPreparedBytes);
        m_Peaks.TerrainLayerDescriptors = Math.Max(
            m_Peaks.TerrainLayerDescriptors,
            current.TerrainLayerDescriptors);
        m_Peaks.SelectedPatches = Math.Max(
            m_Peaks.SelectedPatches,
            current.SelectedPatches);
        m_Peaks.PendingDisposals = Math.Max(
            m_Peaks.PendingDisposals,
            current.PendingDisposals);
    }

    private TerrainStreamingMemorySnapshot CaptureMemory()
    {
        LoadedCookedAssetDiagnostic[] handles =
            m_AssetDatabase.GetLoadedCookedAssetDiagnostics().ToArray();
        RuntimeAssetResidencyMetrics residency = m_Residency.GetMetrics();
        TerrainDiagnosticsSnapshot terrain = m_Diagnostics.GetSnapshot();
        TerrainResidencyMetrics terrainResidency = terrain.Residency;
        long terrainCpuBytes = checked(
            terrainResidency.CpuHeightBytes +
            terrainResidency.CpuWeightBytes +
            terrainResidency.CpuErrorBytes);
        long terrainPreparedBytes = checked(
            terrainResidency.PreparedHeightBytes +
            terrainResidency.PreparedWeightBytes +
            terrainResidency.PreparedErrorBytes +
            terrainResidency.PreparedLayerBytes);
        return new TerrainStreamingMemorySnapshot(
            m_EntityManager?.AllocatedSlotCount ?? 0,
            handles.Length,
            handles.Sum(handle => handle.SizeInBytes),
            residency.ResidentAssetCount,
            residency.PreparedGpuBytes,
            residency.PreparedDescriptorCount,
            terrainCpuBytes,
            terrainPreparedBytes,
            terrainResidency.LayerDescriptorCount,
            terrain.Lod.SelectedPatchCount,
            terrainResidency.PendingDisposalCount);
    }

    private void SelectPath(WorldDescriptor world)
    {
        WorldCellDescriptor discovery = world.Cells
            .OrderBy(cell => Math.Abs((long)cell.Key.Coordinate.X))
            .ThenBy(cell => Math.Abs((long)cell.Key.Coordinate.Z))
            .ThenBy(cell => cell.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Terrain-streaming smoke requires at least one world cell.");
        m_DiscoverySource = GetCellCenter(world.Partition, discovery.Key.Coordinate);
        int farX = checked(
            world.Cells.Max(cell => cell.Key.Coordinate.X) +
            world.Partition.LoadRadius +
            world.Partition.UnloadHysteresis +
            4);
        m_RebaseSource = GetCellCenter(
            world.Partition,
            new WorldCellCoordinate(
                farX,
                discovery.Key.Coordinate.Y,
                discovery.Key.Coordinate.Z));
    }

    private void ConfigureValidationBudgets(WorldDescriptor world)
    {
        long largestCell = Math.Max(1, world.Cells.Max(cell => cell.EstimatedCpuBytes));
        var budgets = new WorldStreamingBudgets(
            MaxConcurrentReads: 1,
            MaxBytesInFlight: Math.Max(64L * 1024 * 1024, largestCell),
            MaxDecodedStagingBytes: Math.Max(64L * 1024 * 1024, largestCell),
            MaxActivationsPerFrame: 1,
            MaxActivationMilliseconds: 100.0,
            MaxUnloadsPerFrame: 1);
        if (!m_Streaming.TryConfigureBudgets(budgets, out string diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private void CaptureCamera()
    {
        ComponentPool<CameraComponent> cameras = m_EntityManager!.GetPool<CameraComponent>();
        ComponentPool<TransformComponent> transforms =
            m_EntityManager.GetPool<TransformComponent>();
        ReadOnlySpan<Entity> entities = cameras.GetRawEntityArray();
        bool found = false;
        for (int index = 0; index < cameras.Count; index++)
        {
            Entity entity = entities[index];
            if (!m_EntityManager.IsAlive(entity) || !transforms.Has(entity)) continue;
            if (found)
            {
                throw new InvalidOperationException(
                    "Terrain-streaming smoke requires exactly one persistent camera.");
            }

            ref TransformComponent transform = ref transforms.GetRef(entity);
            if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation))
            {
                throw new InvalidOperationException(
                    "Terrain-streaming camera transform is not finite.");
            }

            found = true;
            m_CameraEntity = entity;
            m_OriginalCameraPosition = m_Origin.ToWorld(transform.Position);
            m_CurrentCameraPosition = m_OriginalCameraPosition;
        }

        if (!found)
        {
            throw new InvalidOperationException(
                "Terrain-streaming smoke requires one persistent camera.");
        }
    }

    private void BuildCameraPath(TerrainPatchWorldBounds rootBounds)
    {
        TerrainStreamingCameraPoses poses = TerrainStreamingCameraPath.Build(
            rootBounds,
            m_OriginalCameraPosition,
            SampleSurfaceHeight);
        m_NearCameraPosition = poses.NearPosition;
        m_NearRotation = poses.NearRotation;
        m_BoundaryCameraPosition = poses.BoundaryPosition;
        m_BoundaryRotation = poses.BoundaryRotation;
        m_MirrorCameraPosition = poses.MirrorPosition;
        m_MirrorRotation = poses.MirrorRotation;
        m_FarCameraPosition = poses.FarPosition;
        m_FarRotation = poses.FarRotation;
    }

    /// <summary>
    /// Terrain surface height the fixture's own poses stand on. The runtime query answers only while
    /// the canonical tiles are active, so the discovery pose falls back to the bounds maximum; the
    /// fixture rebuilds its path once the complete render snapshot exists and the query is live.
    /// </summary>
    private double SampleSurfaceHeight(double worldX, double worldZ)
    {
        WorldPosition position = new(worldX, m_OriginalCameraPosition.Y, worldZ);
        TerrainQueryResult result = m_Query.Query(position);
        return result.Status == TerrainQueryStatus.Available && result.SurfacePosition.IsFinite
            ? result.SurfacePosition.Y
            : double.NaN;
    }

    private void SetCamera(WorldPosition worldPosition, in Quaternion rotation)
    {
        if (!m_EntityManager!.IsAlive(m_CameraEntity) ||
            !m_EntityManager.HasComponent<TransformComponent>(m_CameraEntity) ||
            !m_Origin.TryToOriginRelative(worldPosition, out Vector3 relative))
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera could not be represented at the current origin.");
        }

        ref TransformComponent transform = ref m_EntityManager.GetComponent<TransformComponent>(
            m_CameraEntity);
        transform.Position = relative;
        transform.Rotation = rotation;
        m_CurrentCameraPosition = worldPosition;
    }

    private void ScheduleCapture(string name, uint frameIndex)
    {
        m_PendingCapture = name;
        m_PendingCaptureFrame = frameIndex;
        if (m_Context.VisualSummaryService != null &&
            !m_Context.VisualSummaryService.TryScheduleCapture(name, frameIndex, out _))
        {
            ReportFailure($"Could not schedule terrain visual checkpoint '{name}'.");
        }
    }

    private bool CaptureCompleted(string name, uint frameIndex)
    {
        if (!string.Equals(name, m_PendingCapture, StringComparison.Ordinal))
        {
            ReportFailure($"Terrain capture state expected '{m_PendingCapture}', got '{name}'.");
            return false;
        }

        IRuntimeVisualSummaryService? visual = m_Context.VisualSummaryService;
        if (visual == null)
        {
            return frameIndex >= m_PendingCaptureFrame;
        }

        if (!visual.TryGetCaptureResult(name, out RuntimeVisualSummaryCaptureResult result))
        {
            ReportFailure($"Terrain visual checkpoint '{name}' was not registered.");
            return false;
        }

        if (result.State == RuntimeVisualSummaryCaptureState.Failed)
        {
            ReportFailure(result.FailureMessage ?? $"Terrain visual checkpoint '{name}' failed.");
            return false;
        }

        return result.State == RuntimeVisualSummaryCaptureState.Succeeded &&
            m_Diagnostics.GetSnapshot().FrameIndex >= result.Capture.FrameIndex;
    }

    private void OnRebaseStarting(WorldOriginRebase rebase) =>
        m_RebaseStarts.Add(rebase.Sequence);

    private void OnRebased(WorldOriginRebase rebase) =>
        m_RebaseCompletions.Add(rebase.Sequence);

    private void WriteArtifact()
    {
        RuntimeVisualSummaryCaptureResult[] captures =
            m_Context.VisualSummaryService?.GetCaptureResults().ToArray() ?? [];
        var artifact = new TerrainStreamingSmokeArtifact(
            SchemaVersion: 3,
            CapturedAtUtc: DateTime.UtcNow,
            Mode: Name,
            Profile: m_Context.ProfileName,
            WorldGuid: m_World?.WorldGuid ?? Guid.Empty,
            TerrainRootGuid: m_RootGuid,
            TerrainCellIds: m_OwnerCells?.IdStrings() ?? Array.Empty<string>(),
            CoveredTileCount: m_CoveredTiles.Count,
            Passed: Succeeded,
            Failure: m_FailureMessage,
            RequestedSoakCycles: SoakCycleCount,
            CompletedSoakCycles: m_SoakCyclesCompleted,
            RebaseSequences: m_RebaseCompletions.ToArray(),
            Checkpoints: m_Checkpoints.ToArray(),
            VisualCaptures: captures,
            Peaks: m_Peaks,
            ShutdownDrained: m_ShutdownDrained,
            TerminalStage: m_TerminalStage == TerrainStreamingSmokeStage.None
                ? m_Stage
                : m_TerminalStage,
            LastDrain: m_LastDrainSnapshot);
        string? directory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = OutputPath + ".tmp." + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(artifact, ArtifactSerializerOptions);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, OutputPath, overwrite: true);
    }

    /// <summary>
    /// The gate scripts read the artifact by these exact camel-case names. The owner-cell set and
    /// the per-cell drain rows are part of the published contract, so the options are shared with
    /// the tests that pin that contract instead of being rebuilt at the write site.
    /// </summary>
    internal static readonly JsonSerializerOptions ArtifactSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The world cells that hold the tile, as published per checkpoint tile. Non-world-cell owners
    /// (the persistent scene) are not part of the terrain owner-cell contract.
    /// </summary>
    private static string[] TerrainTileOwnerCellIds(TerrainTileDiagnosticSnapshot tile)
    {
        var ids = new List<string>();
        for (int index = 0; index < tile.Owners.Count; index++)
        {
            RuntimeAssetResidencyOwnerId owner = tile.Owners[index];
            if (owner.Kind != RuntimeAssetResidencyOwnerKind.WorldCell || !owner.CellId.IsValid)
            {
                continue;
            }

            string id = owner.CellId.ToString();
            if (!ids.Contains(id)) ids.Add(id);
        }

        ids.Sort(StringComparer.Ordinal);
        return ids.ToArray();
    }

    private static IReadOnlyList<TerrainStreamingLodBucket> BuildLodHistogram(
        IReadOnlyList<TerrainTileDiagnosticSnapshot> tiles)
    {
        return tiles
            .SelectMany(tile => tile.Patches)
            .GroupBy(patch => patch.LodLevel)
            .OrderBy(group => group.Key)
            .Select(group => new TerrainStreamingLodBucket(group.Key, group.Count()))
            .ToArray();
    }

    private static bool Contains(
        in TerrainPatchWorldBounds outer,
        in TerrainPatchWorldBounds inner) =>
        inner.Min.X >= outer.Min.X - PositionEpsilon &&
        inner.Min.Y >= outer.Min.Y - PositionEpsilon &&
        inner.Min.Z >= outer.Min.Z - PositionEpsilon &&
        inner.Max.X <= outer.Max.X + PositionEpsilon &&
        inner.Max.Y <= outer.Max.Y + PositionEpsilon &&
        inner.Max.Z <= outer.Max.Z + PositionEpsilon;

    private static WorldPosition Center(in TerrainPatchWorldBounds bounds) => new(
        (bounds.Min.X + bounds.Max.X) * 0.5,
        (bounds.Min.Y + bounds.Max.Y) * 0.5,
        (bounds.Min.Z + bounds.Max.Z) * 0.5);

    private static WorldPosition GetCellCenter(
        WorldPartitionSettings partition,
        WorldCellCoordinate coordinate)
    {
        WorldPosition origin = WorldPartitionCoordinates.GetCellOrigin(partition, coordinate);
        return new WorldPosition(
            origin.X + partition.CellSize.X * 0.5,
            origin.Y + partition.CellSize.Y * 0.5,
            origin.Z + partition.CellSize.Z * 0.5);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static string GetDefaultOutputPath(string workspacePath, string profileName)
    {
        string safeProfile = string.Concat(profileName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.GetFullPath(Path.Combine(
            workspacePath,
            ".arisen",
            "Logs",
            $"terrain-streaming-summary-{safeProfile}-latest.json"));
    }
}

internal enum TerrainStreamingSmokeStage
{
    None,
    AwaitStartupWorld,
    AwaitDiscovery,
    AwaitNearCapture,
    AwaitBoundaryCapture,
    AwaitMirrorCapture,
    AwaitFarCapture,
    AwaitOriginRebase,
    AwaitPostRebaseCapture,
    AwaitReturnedCapture,
    AwaitInitialUnload,
    AwaitSoakLoad,
    AwaitSoakReload,
    AwaitSoakUnload,
    ReadyForShutdown
}

internal sealed record TerrainStreamingSmokeArtifact(
    int SchemaVersion,
    DateTime CapturedAtUtc,
    string Mode,
    string Profile,
    Guid WorldGuid,
    Guid TerrainRootGuid,
    IReadOnlyList<string> TerrainCellIds,
    int CoveredTileCount,
    bool Passed,
    string? Failure,
    int RequestedSoakCycles,
    int CompletedSoakCycles,
    IReadOnlyList<long> RebaseSequences,
    IReadOnlyList<TerrainStreamingSmokeCheckpoint> Checkpoints,
    IReadOnlyList<RuntimeVisualSummaryCaptureResult> VisualCaptures,
    TerrainStreamingSmokePeaks Peaks,
    bool ShutdownDrained,
    TerrainStreamingSmokeStage TerminalStage,
    TerrainStreamingDrainSnapshot LastDrain);

internal sealed record TerrainStreamingSmokeCheckpoint(
    string Name,
    uint FrameIndex,
    WorldPosition CameraWorldPosition,
    WorldOriginSnapshot Origin,
    Guid TerrainRootGuid,
    IReadOnlyList<string> TerrainCellIds,
    IReadOnlyList<TerrainStreamingTileSnapshot> Tiles,
    int ExpectedTileCount,
    IReadOnlyList<TerrainStreamingLodBucket> LodHistogram,
    TerrainLodMetrics Lod,
    int SeamViolationCount,
    int EcsTileCount,
    IReadOnlyList<TerrainStreamingQuerySample> QuerySamples,
    TerrainStreamingMemorySnapshot Memory,
    bool Passed);

internal sealed record TerrainStreamingTileSnapshot(
    Guid TileGuid,
    TerrainTileCoordinate Coordinate,
    ulong Generation,
    int MinimumLod,
    int MaximumLod,
    int PatchCount,
    bool FrustumVisible,
    TerrainPatchWorldBounds WorldBounds,
    int SeamViolationCount,
    IReadOnlyList<string> OwnerCellIds);

internal sealed record TerrainStreamingLodBucket(int Level, int PatchCount);

internal sealed record TerrainStreamingQuerySample(
    Guid TileGuid,
    TerrainTileCoordinate Coordinate,
    TerrainQueryStatus Status,
    ulong Generation,
    WorldPosition SurfacePosition,
    Vector3 Normal,
    Vector4 LayerWeights,
    bool Passed);

internal sealed record TerrainStreamingMemorySnapshot(
    int AllocatedEntitySlots,
    int LoadedCookedHandles,
    long LoadedCookedBytes,
    int ResidentAssets,
    long PreparedGpuBytes,
    int PreparedDescriptors,
    long TerrainCpuBytes,
    long TerrainPreparedBytes,
    int TerrainLayerDescriptors,
    int SelectedPatches,
    int PendingDisposals);

internal sealed record TerrainStreamingSmokeBounds(
    int AllocatedEntitySlots,
    int LoadedCookedHandles,
    int ResidentAssets,
    int PreparedDescriptors,
    long TerrainCpuBytes,
    long TerrainPreparedBytes,
    int TerrainLayerDescriptors,
    int SelectedPatches);

/// <summary>
/// One owner cell of the resident root at a drain boundary. An owner cell the streaming service no
/// longer tracks publishes <see cref="Tracked"/> false and a null state instead of a fabricated
/// state, and never counts as drained.
/// </summary>
internal readonly record struct TerrainStreamingCellDrainSnapshot(
    string CellId,
    bool Tracked,
    WorldCellStreamingState? State,
    bool Desired,
    WorldCellDesiredSource DesiredSources,
    bool Pinned)
{
    public static TerrainStreamingCellDrainSnapshot Untracked(WorldCellId cellId) => new(
        cellId.ToString(),
        Tracked: false,
        State: null,
        Desired: false,
        DesiredSources: WorldCellDesiredSource.None,
        Pinned: false);

    public bool IsDrained =>
        Tracked &&
        State is WorldCellStreamingState.Unloaded or WorldCellStreamingState.Cancelled &&
        !Desired &&
        !Pinned &&
        DesiredSources == WorldCellDesiredSource.None;
}

internal readonly record struct TerrainStreamingDrainSnapshot(
    IReadOnlyList<TerrainStreamingCellDrainSnapshot> Cells,
    int VisibleTileCount,
    int RuntimeRootCount,
    int RuntimeTileCount,
    int DiagnosticRootCount,
    int DiagnosticTileCount,
    int DiagnosticResourceCount,
    int TerrainResidencyResourceCount,
    int TerrainPendingDisposalCount,
    int TotalPendingDisposalCount,
    int OutstandingTaskCount)
{
    public bool IsDrained =>
        Cells != null &&
        Cells.Count > 0 &&
        Cells.All(cell => cell.IsDrained) &&
        VisibleTileCount == 0 &&
        RuntimeRootCount == 0 &&
        RuntimeTileCount == 0 &&
        DiagnosticRootCount == 0 &&
        DiagnosticTileCount == 0 &&
        DiagnosticResourceCount == 0 &&
        TerrainResidencyResourceCount == 0 &&
        TerrainPendingDisposalCount == 0 &&
        TotalPendingDisposalCount == 0 &&
        OutstandingTaskCount == 0;
}

internal sealed class TerrainStreamingSmokePeaks
{
    public int AllocatedEntitySlots { get; set; }
    public int LoadedCookedHandles { get; set; }
    public long LoadedCookedBytes { get; set; }
    public int ResidentAssets { get; set; }
    public long PreparedGpuBytes { get; set; }
    public int PreparedDescriptors { get; set; }
    public long TerrainCpuBytes { get; set; }
    public long TerrainPreparedBytes { get; set; }
    public int TerrainLayerDescriptors { get; set; }
    public int SelectedPatches { get; set; }
    public int PendingDisposals { get; set; }
}
