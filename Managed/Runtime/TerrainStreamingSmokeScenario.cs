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
    private const float FarCameraRetreat = 48.0f;

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
    private WorldCellDescriptor? m_TerrainCell;
    private WorldPosition m_DiscoverySource;
    private WorldPosition m_RebaseSource;
    private Entity m_CameraEntity;
    private WorldPosition m_OriginalCameraPosition;
    private WorldPosition m_BoundaryCameraPosition;
    private WorldPosition m_FarCameraPosition;
    private WorldPosition m_CurrentCameraPosition;
    private Quaternion m_CameraRotation;
    private Guid m_RootGuid;
    private int m_ExpectedTileCount;
    private int m_ExpectedLayerCount;
    private long m_InitialRebaseSequence;
    private long m_ReloadGeneration;
    private string m_PendingCapture = string.Empty;
    private uint m_PendingCaptureFrame;
    private TerrainStreamingSmokeBounds? m_LoadedBounds;
    private TerrainStreamingDrainSnapshot m_LastDrainSnapshot;
    private TerrainStreamingSmokeStage m_Stage;
    private TerrainStreamingSmokeStage m_TerminalStage;
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

        m_TerminalStage = m_Stage;
        m_FailureMessage ??= message;
        m_Context.VisualSummaryService?.Seal();
        m_ReadyForShutdown = true;
        m_Stage = TerrainStreamingSmokeStage.ReadyForShutdown;
    }

    public void AfterShutdown()
    {
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
                (captures.Length != 5 || captures.Any(capture =>
                    capture.State != RuntimeVisualSummaryCaptureState.Succeeded)))
            {
                m_FailureMessage ??=
                    "Terrain visual validation did not complete all five named captures.";
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
        if (!IsTerrainReady(snapshot)) return;

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
        if (!HasCompleteRenderSnapshot(snapshot, root, tiles)) return;

        WorldCellId[] ownerCells = tiles
            .SelectMany(tile => tile.Owners)
            .Where(owner => owner.Kind == RuntimeAssetResidencyOwnerKind.WorldCell)
            .Select(owner => owner.CellId)
            .Distinct()
            .Order()
            .ToArray();
        if (ownerCells.Length != 1)
        {
            ReportFailure(
                "Terrain-streaming fixture must attribute every canonical tile to one world cell.");
            return;
        }

        m_TerrainCell = m_World!.Cells.SingleOrDefault(cell => cell.Id == ownerCells[0]);
        if (m_TerrainCell == null || !m_Streaming.PinCell(m_TerrainCell.Id))
        {
            ReportFailure("Could not pin the discovered terrain owner cell.");
            return;
        }

        m_RootGuid = root.RootGuid;
        m_ExpectedTileCount = root.TileCount;
        m_ExpectedLayerCount = root.Layers.Count;
        m_InitialRebaseSequence = m_Origin.RebaseSequence;
        BuildCameraPath(root.WorldBounds);
        m_Streaming.ClearStreamingSource();
        SetCamera(m_OriginalCameraPosition);
        ScheduleCapture("near", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitNearCapture;
    }

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
                tile.Patches.Count == 0)
            {
                return false;
            }

            patchCount = checked(patchCount + tile.Patches.Count);
        }

        return patchCount == snapshot.Lod.SelectedPatchCount;
    }

    private void BeginBoundaryCapture(uint frameIndex)
    {
        SetCamera(m_BoundaryCameraPosition);
        m_Streaming.SetStreamingSource(m_BoundaryCameraPosition);
        ScheduleCapture("boundary-mixed-lod", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitBoundaryCapture;
    }

    private void BeginFarCapture(uint frameIndex)
    {
        SetCamera(m_FarCameraPosition);
        m_Streaming.SetStreamingSource(m_FarCameraPosition);
        ScheduleCapture("far-cascade", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitFarCapture;
    }

    private void BeginOriginRebase()
    {
        SetCamera(m_RebaseSource);
        m_Streaming.SetStreamingSource(m_RebaseSource);
        m_Stage = TerrainStreamingSmokeStage.AwaitOriginRebase;
    }

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
        SetCamera(m_BoundaryCameraPosition);
        ScheduleCapture("post-rebase", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitPostRebaseCapture;
    }

    private void BeginReturnedCapture(uint frameIndex)
    {
        SetCamera(m_OriginalCameraPosition);
        ScheduleCapture("returned-start", checked(frameIndex + 1));
        m_Stage = TerrainStreamingSmokeStage.AwaitReturnedCapture;
    }

    private void BeginInitialUnload()
    {
        m_Streaming.ClearStreamingSource();
        if (m_TerrainCell == null || !m_Streaming.UnpinCell(m_TerrainCell.Id))
        {
            ReportFailure("Could not unpin the terrain cell before soak validation.");
            return;
        }

        m_Stage = TerrainStreamingSmokeStage.AwaitInitialUnload;
    }

    private void BeginSoakLoad()
    {
        if (m_TerrainCell == null || !m_Streaming.PinCell(m_TerrainCell.Id))
        {
            ReportFailure("Could not pin the terrain cell for soak validation.");
            return;
        }

        m_Stage = TerrainStreamingSmokeStage.AwaitSoakLoad;
    }

    private void ObserveSoakLoad(uint frameIndex)
    {
        if (!TerrainCellReady(out WorldCellStreamingSnapshot cell)) return;
        CaptureCheckpoint($"soak-load-{m_SoakCyclesCompleted + 1}", frameIndex);
        if (m_FailureMessage != null) return;

        m_ReloadGeneration = cell.RequestGeneration;
        if (!m_Streaming.RequestCellReload(cell.CellId))
        {
            ReportFailure("Terrain soak could not request an active-cell reload.");
            return;
        }

        m_Stage = TerrainStreamingSmokeStage.AwaitSoakReload;
    }

    private void ObserveSoakReload(uint frameIndex)
    {
        if (!TerrainCellReady(out WorldCellStreamingSnapshot cell) ||
            cell.RequestGeneration <= m_ReloadGeneration)
        {
            return;
        }

        TerrainDiagnosticsSnapshot snapshot = m_Diagnostics.GetSnapshot();
        bool currentOwnerGeneration = snapshot.Tiles
            .Where(tile => tile.TerrainRootGuid == m_RootGuid)
            .All(tile => tile.Owners.Any(owner =>
                owner.CellId == cell.CellId && owner.Generation == cell.RequestGeneration));
        if (!currentOwnerGeneration) return;

        CaptureCheckpoint($"soak-reload-{m_SoakCyclesCompleted + 1}", frameIndex);
        if (m_FailureMessage != null) return;
        if (!m_Streaming.UnpinCell(cell.CellId))
        {
            ReportFailure("Terrain soak could not unpin the reloaded cell.");
            return;
        }

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
        TerrainTileComponent[] components = m_RenderSource.ExtractVisibleTiles().ToArray();
        TerrainRootDiagnosticSnapshot? root = snapshot.Roots.SingleOrDefault(candidate =>
            candidate.RootGuid == m_RootGuid);
        TerrainTileDiagnosticSnapshot[] tiles = snapshot.Tiles
            .Where(tile => tile.TerrainRootGuid == m_RootGuid)
            .OrderBy(tile => tile.Coordinate.Z)
            .ThenBy(tile => tile.Coordinate.X)
            .ThenBy(tile => tile.TileGuid)
            .ToArray();
        bool valid = root != null &&
            IsTerrainReady(snapshot) &&
            components.Length == m_ExpectedTileCount &&
            components.Select(component => component.TileGuid).Distinct().Count() ==
                m_ExpectedTileCount &&
            components.All(component => component.TerrainRootGuid == m_RootGuid) &&
            tiles.Length == m_ExpectedTileCount &&
            tiles.All(tile => ValidateTile(tile)) &&
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
            m_TerrainCell?.Id.ToString() ?? string.Empty,
            tiles.Select(tile => new TerrainStreamingTileSnapshot(
                tile.TileGuid,
                tile.Coordinate,
                tile.Generation,
                tile.MinimumSelectedLod,
                tile.MaximumSelectedLod,
                tile.Patches.Count,
                tile.WorldBounds,
                tile.SeamViolationCount)).ToArray(),
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
                $"Terrain checkpoint '{name}' found invalid residency, LOD, patch bounds, " +
                "ECS ownership, query parity, or seam state.");
            return checkpoint;
        }

        ValidateLoadedBounds(memory);
        return checkpoint;
    }

    private bool ValidateTile(TerrainTileDiagnosticSnapshot tile)
    {
        if (tile.ResidencyState != RuntimePreparedAssetState.Ready ||
            !tile.IsVisible ||
            tile.IsFailed ||
            tile.SeamViolationCount != 0 ||
            !tile.WorldBounds.IsValid ||
            !tile.Owners.Any(owner =>
                owner.Kind == RuntimeAssetResidencyOwnerKind.WorldCell &&
                owner.CellId == m_TerrainCell!.Id))
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

    private bool TerrainCellReady(out WorldCellStreamingSnapshot cell)
    {
        cell = m_TerrainCell == null
            ? null!
            : m_Streaming.GetCells().Single(candidate => candidate.CellId == m_TerrainCell.Id);
        TerrainDiagnosticsSnapshot snapshot = m_Diagnostics.GetSnapshot();
        return cell != null &&
            cell.State == WorldCellStreamingState.Active &&
            cell.Pinned &&
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
    }

    private bool TerrainDrained()
    {
        if (m_TerrainCell == null) return false;
        WorldCellStreamingSnapshot cell = m_Streaming.GetCells()
            .Single(candidate => candidate.CellId == m_TerrainCell.Id);
        TerrainDiagnosticsSnapshot diagnostics = m_Diagnostics.GetSnapshot();
        TerrainRuntimeDataMetrics runtimeData = m_RuntimeData.GetMetrics();
        RuntimeAssetResidencySnapshot[] terrainResources = m_Residency.GetResources()
            .Where(resource => resource.Key.AssetType is "TerrainRoot" or "TerrainTile")
            .ToArray();
        m_LastDrainSnapshot = new TerrainStreamingDrainSnapshot(
            cell.State,
            cell.Desired,
            cell.DesiredSources,
            cell.Pinned,
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
        if (m_LoadedBounds == null)
        {
            m_LoadedBounds = current;
            return;
        }

        if (current.AllocatedEntitySlots > m_LoadedBounds.AllocatedEntitySlots ||
            current.LoadedCookedHandles > m_LoadedBounds.LoadedCookedHandles ||
            current.ResidentAssets > m_LoadedBounds.ResidentAssets ||
            current.PreparedDescriptors > m_LoadedBounds.PreparedDescriptors ||
            current.TerrainCpuBytes > m_LoadedBounds.TerrainCpuBytes ||
            current.TerrainPreparedBytes > m_LoadedBounds.TerrainPreparedBytes ||
            current.TerrainLayerDescriptors > m_LoadedBounds.TerrainLayerDescriptors ||
            current.SelectedPatches > TerrainLodSettings.Default.MaximumPatchCount)
        {
            ReportFailure("Terrain reload soak exceeded its first loaded steady-state bounds.");
        }
    }

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
            m_CameraRotation = transform.Rotation;
        }

        if (!found)
        {
            throw new InvalidOperationException(
                "Terrain-streaming smoke requires one persistent camera.");
        }
    }

    private void BuildCameraPath(TerrainPatchWorldBounds rootBounds)
    {
        double centerX = (rootBounds.Min.X + rootBounds.Max.X) * 0.5;
        m_BoundaryCameraPosition = new WorldPosition(
            centerX,
            m_OriginalCameraPosition.Y,
            m_OriginalCameraPosition.Z);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, m_CameraRotation);
        float lengthSquared = forward.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0.000001f)
        {
            throw new InvalidOperationException("Terrain-streaming camera direction is invalid.");
        }

        forward /= MathF.Sqrt(lengthSquared);
        m_FarCameraPosition = new WorldPosition(
            m_OriginalCameraPosition.X - forward.X * FarCameraRetreat,
            m_OriginalCameraPosition.Y - forward.Y * FarCameraRetreat,
            m_OriginalCameraPosition.Z - forward.Z * FarCameraRetreat);
    }

    private void SetCamera(WorldPosition worldPosition)
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
        transform.Rotation = m_CameraRotation;
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
            SchemaVersion: 1,
            CapturedAtUtc: DateTime.UtcNow,
            Mode: Name,
            Profile: m_Context.ProfileName,
            WorldGuid: m_World?.WorldGuid ?? Guid.Empty,
            TerrainRootGuid: m_RootGuid,
            TerrainCellId: m_TerrainCell?.Id.ToString() ?? string.Empty,
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
        string json = JsonSerializer.Serialize(artifact, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, OutputPath, overwrite: true);
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
    string TerrainCellId,
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
    string TerrainCellId,
    IReadOnlyList<TerrainStreamingTileSnapshot> Tiles,
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
    TerrainPatchWorldBounds WorldBounds,
    int SeamViolationCount);

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

internal readonly record struct TerrainStreamingDrainSnapshot(
    WorldCellStreamingState CellState,
    bool CellDesired,
    WorldCellDesiredSource CellDesiredSources,
    bool CellPinned,
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
        CellState is WorldCellStreamingState.Unloaded or WorldCellStreamingState.Cancelled &&
        !CellDesired &&
        !CellPinned &&
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
