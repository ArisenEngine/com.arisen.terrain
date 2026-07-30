using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Terrain.Assets;

public sealed class TerrainRuntimeAssetCooker : IRuntimeAssetCooker
{
    private readonly IAssetDatabase m_AssetDatabase;

    public TerrainRuntimeAssetCooker(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    public string ProviderId => "com.arisen.terrain.runtime-asset-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } =
    [
        TerrainAssetTypes.Root,
        TerrainAssetTypes.Tile
    ];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Terrain.CookAsset");
        ArgumentNullException.ThrowIfNull(context);
        return request.AssetType switch
        {
            TerrainAssetTypes.Root => CookRoot(context, request),
            TerrainAssetTypes.Tile => CookTile(context, request),
            _ => throw new InvalidOperationException(
                $"[TerrainRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.")
        };
    }

    private RuntimeAssetCookerOutput CookRoot(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Terrain.CookRoot");
        ValidateVariant(request, TerrainRootAssetCooker.RuntimeVariant);
        ValidateRootOwnership(request);
        var rootRef = new AssetRef<TerrainRootSourceAsset>(
            request.Guid,
            TerrainAssetTypes.Root,
            request.PackageId);
        CookedTerrainRoot? previousRoot =
            TerrainRootAssetCooker.TryLoadPreviousCookedRoot(m_AssetDatabase, rootRef);
        InvalidateIfForced(context, request.Guid, TerrainRootAssetCooker.RuntimeVariant);
        CookedTerrainRootArtifact cooked = TerrainRootAssetCooker.Cook(
            m_AssetDatabase,
            rootRef,
            previousRoot);
        RuntimeAssetCookDependencyRequest[] dependencies = cooked.Dependencies
            .Select(dependency => new RuntimeAssetCookDependencyRequest(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant,
                dependency.Required))
            .ToArray();
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            BuildOutputRelativePath(
                request.PackageId,
                request.Guid,
                cooked.Variant,
                TerrainRootAssetCooker.CookedExtension),
            cooked.Path,
            TerrainRootAssetCooker.CookedFormatVersion,
            dependencies);
    }

    private RuntimeAssetCookerOutput CookTile(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Terrain.CookTile");
        ValidateVariant(request, TerrainTileAssetCooker.RuntimeVariant);
        InvalidateIfForced(context, request.Guid, TerrainTileAssetCooker.RuntimeVariant);
        CookedTerrainTileArtifact cooked = TerrainTileAssetCooker.Cook(
            m_AssetDatabase,
            request.Guid,
            request.PackageId);
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            BuildOutputRelativePath(
                request.PackageId,
                request.Guid,
                cooked.Variant,
                TerrainTileAssetCooker.CookedExtension),
            cooked.Path,
            TerrainTileAssetCooker.CookedFormatVersion);
    }

    private void ValidateRootOwnership(RuntimeAssetCookRequest request)
    {
        if (!m_AssetDatabase.TryGetAsset(request.Guid, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, TerrainAssetTypes.Root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceAsset.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainRuntimeAssetCooker] Terrain root '{request.Guid:D}' is not owned by package '{request.PackageId}'.");
        }
    }

    private static void ValidateVariant(RuntimeAssetCookRequest request, string expectedVariant)
    {
        if (request.Variant.Length > 0 &&
            !string.Equals(request.Variant, expectedVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[TerrainRuntimeAssetCooker] Asset '{request.Guid:D}' variant '{request.Variant}' is unsupported; " +
                $"expected '{expectedVariant}'.");
        }
    }

    private void InvalidateIfForced(
        RuntimeAssetCookContext context,
        Guid guid,
        string variant)
    {
        if (context.ForceRebuild)
        {
            m_AssetDatabase.InvalidateCookedAssets(guid, variant);
        }
    }

    private static string BuildOutputRelativePath(
        string packageId,
        Guid guid,
        string variant,
        string extension)
    {
        return $"{packageId}/{guid:N}/{variant}{extension}";
    }
}
