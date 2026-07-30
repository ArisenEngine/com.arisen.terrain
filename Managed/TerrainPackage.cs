using ArisenEngine.Core.Assets;
using ArisenEngine.ECS.Lifecycle;
using ArisenEngine.Terrain.Assets;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Lifecycle;
using ArisenEngine.Threading;

namespace ArisenEngine.Terrain;

public sealed class TerrainPackage : IPackageEntry
{
    private TerrainRuntimeAssetCooker? m_RuntimeAssetCooker;
    private TerrainTileSceneComponentCodec? m_SceneComponentCodec;
    private ISceneComponentExtensionRegistry? m_SceneComponentRegistry;
    private TerrainTileRenderSource? m_RenderSource;
    private TerrainRuntimeDataStore? m_RuntimeData;
    private TerrainQueryService? m_QueryService;
    private TerrainLodPlanner? m_LodPlanner;
    private TerrainDiagnosticsService? m_Diagnostics;
    private TerrainAuthoringPreviewService? m_AuthoringPreviews;
    private IRuntimeSmokeScenarioRegistry? m_SmokeScenarioRegistry;
    private TerrainStreamingSmokeScenarioProvider? m_SmokeScenarioProvider;

    public void OnLoad(IServiceRegistry services)
    {
        IAssetDatabase assetDatabase = services.GetService<IAssetDatabase>();
        m_RuntimeAssetCooker = new TerrainRuntimeAssetCooker(assetDatabase);
        services.GetService<IRuntimeAssetCookerRegistry>().RegisterCooker(m_RuntimeAssetCooker);
        m_SceneComponentRegistry = services.GetService<ISceneComponentExtensionRegistry>();
        m_SceneComponentCodec = new TerrainTileSceneComponentCodec();
        m_SceneComponentRegistry.Register(m_SceneComponentCodec);
        SceneSubsystem sceneSubsystem = EngineKernel.Instance.GetSubsystem<SceneSubsystem>()
            ?? throw new InvalidOperationException(
                "Terrain runtime requires the active SceneSubsystem.");
        m_RenderSource = new TerrainTileRenderSource(
            () => sceneSubsystem.ActiveEntityManager);
        m_RuntimeData = new TerrainRuntimeDataStore();
        m_QueryService = new TerrainQueryService(
            m_RuntimeData,
            () => sceneSubsystem.ActiveEntityManager);
        m_LodPlanner = new TerrainLodPlanner(m_RuntimeData);
        m_Diagnostics = new TerrainDiagnosticsService();
        m_AuthoringPreviews = new TerrainAuthoringPreviewService();
        services.RegisterService<ITerrainTileRenderSource>(m_RenderSource);
        services.RegisterService<ITerrainRuntimeDataStore>(m_RuntimeData);
        services.RegisterService<ITerrainQueryService>(m_QueryService);
        services.RegisterService<ITerrainLodPlanner>(m_LodPlanner);
        services.RegisterService<ITerrainDiagnostics>(m_Diagnostics);
        services.RegisterService<ITerrainDiagnosticsPublisher>(m_Diagnostics);
        services.RegisterService<ITerrainResidencyDiagnostics>(m_Diagnostics);
        services.RegisterService<ITerrainAuthoringPreviewService>(m_AuthoringPreviews);
        m_SmokeScenarioRegistry = services.GetService<IRuntimeSmokeScenarioRegistry>();
        m_SmokeScenarioProvider = new TerrainStreamingSmokeScenarioProvider(
            services.GetService<IRuntimeWorldStreamingService>(),
            services.GetService<IRuntimeSceneService>(),
            services.GetService<IRuntimeAssetResidencyService>(),
            services.GetService<IWorldOriginService>(),
            assetDatabase,
            services.GetService<IBackgroundTaskScheduler>(),
            m_RenderSource,
            m_RuntimeData,
            m_QueryService,
            m_Diagnostics);
        m_SmokeScenarioRegistry.Register("terrain-streaming", m_SmokeScenarioProvider);
        KernelLog.Info("[Terrain] Runtime package loaded.");
    }

    public void OnUnload(IServiceRegistry services)
    {
        if (m_SmokeScenarioRegistry != null && m_SmokeScenarioProvider != null)
        {
            m_SmokeScenarioRegistry.Unregister("terrain-streaming", m_SmokeScenarioProvider);
        }
        m_SmokeScenarioProvider = null;
        m_SmokeScenarioRegistry = null;
        if (m_SceneComponentCodec != null)
        {
            m_SceneComponentRegistry?.Unregister(m_SceneComponentCodec);
        }
        m_SceneComponentCodec = null;
        m_SceneComponentRegistry = null;
        m_LodPlanner?.Reset();
        m_Diagnostics?.Clear();
        m_AuthoringPreviews?.Clear();
        m_RuntimeData?.Clear();
        m_AuthoringPreviews = null;
        m_Diagnostics = null;
        m_LodPlanner = null;
        m_QueryService = null;
        m_RuntimeData = null;
        m_RenderSource = null;
        m_RuntimeAssetCooker = null;
        KernelLog.Info("[Terrain] Runtime package unloaded.");
    }
}
