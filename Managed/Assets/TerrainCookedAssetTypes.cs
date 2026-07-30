using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain.Assets;

public readonly record struct TerrainCookedAssetDependency(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant,
    bool Required);

public sealed record CookedTerrainRootArtifact(
    Guid RootGuid,
    string Variant,
    string Path,
    int TileCount,
    int LayerCount,
    long SizeInBytes,
    IReadOnlyList<TerrainCookedAssetDependency> Dependencies);

public sealed record CookedTerrainTileArtifact(
    Guid TileGuid,
    Guid RootGuid,
    TerrainTileCoordinate Coordinate,
    string Variant,
    string Path,
    long SizeInBytes,
    double MinHeight,
    double MaxHeight,
    byte[] ContentHash);

public readonly record struct CookedTerrainTextureReference(
    Guid Guid,
    string PackageId)
{
    public AssetRef<Texture2DSourceAsset> ToAssetRef() =>
        new(Guid, "Texture2D", PackageId);
}

public sealed record CookedTerrainLayer(
    string Id,
    CookedTerrainTextureReference Albedo,
    CookedTerrainTextureReference Normal,
    CookedTerrainTextureReference Orm,
    TerrainLayerTint Tint,
    float RoughnessMultiplier,
    float MetallicMultiplier,
    float NormalStrength,
    TerrainLayerWorldTiling WorldTiling);

public readonly record struct TerrainTileNeighborSet(
    Guid NegativeX,
    Guid PositiveX,
    Guid NegativeZ,
    Guid PositiveZ);

public sealed record CookedTerrainTileReference(
    TerrainTileCoordinate Coordinate,
    Guid Guid,
    TerrainTileNeighborSet Neighbors,
    double MinHeight,
    double MaxHeight,
    long PayloadBytes,
    byte[] ContentHash);

public sealed record CookedTerrainRoot(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    WorldPosition WorldPlacement,
    TerrainSampleSpacing SampleSpacing,
    TerrainHeightRange HeightRange,
    int HeightSourceWidth,
    int HeightSourceHeight,
    int TileResolution,
    TerrainBorderPolicy BorderPolicy,
    TerrainTileCoordinate TileOrigin,
    Guid LayerSetGuid,
    string LayerSetPackageId,
    IReadOnlyList<CookedTerrainLayer> Layers,
    IReadOnlyList<CookedTerrainTileReference> Tiles);

public readonly record struct TerrainGeometricErrorLevel(
    int Level,
    int SampleStep,
    double MaxError);

public sealed class CookedTerrainTile
{
    private readonly ushort[] m_Heights;
    private readonly byte[] m_LayerWeights;
    private readonly TerrainGeometricErrorLevel[] m_GeometricErrors;

    internal CookedTerrainTile(
        Guid guid,
        Guid rootGuid,
        Guid layerSetGuid,
        string packageId,
        int sourceSchemaVersion,
        TerrainTileCoordinate coordinate,
        int resolution,
        int layerCount,
        WorldPosition worldPlacement,
        TerrainSampleSpacing sampleSpacing,
        TerrainHeightRange heightRange,
        double minHeight,
        double maxHeight,
        TerrainBorderPolicy borderPolicy,
        int sourceSampleOffsetX,
        int sourceSampleOffsetZ,
        ushort[] heights,
        byte[] layerWeights,
        TerrainGeometricErrorLevel[] geometricErrors)
    {
        Guid = guid;
        RootGuid = rootGuid;
        LayerSetGuid = layerSetGuid;
        PackageId = packageId;
        SourceSchemaVersion = sourceSchemaVersion;
        Coordinate = coordinate;
        Resolution = resolution;
        LayerCount = layerCount;
        WorldPlacement = worldPlacement;
        SampleSpacing = sampleSpacing;
        HeightRange = heightRange;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
        BorderPolicy = borderPolicy;
        SourceSampleOffsetX = sourceSampleOffsetX;
        SourceSampleOffsetZ = sourceSampleOffsetZ;
        m_Heights = heights;
        m_LayerWeights = layerWeights;
        m_GeometricErrors = geometricErrors;
    }

    public Guid Guid { get; }

    public Guid RootGuid { get; }

    public Guid LayerSetGuid { get; }

    public string PackageId { get; }

    public int SourceSchemaVersion { get; }

    public TerrainTileCoordinate Coordinate { get; }

    public int Resolution { get; }

    public int LayerCount { get; }

    public WorldPosition WorldPlacement { get; }

    public TerrainSampleSpacing SampleSpacing { get; }

    public TerrainHeightRange HeightRange { get; }

    public double MinHeight { get; }

    public double MaxHeight { get; }

    public TerrainBorderPolicy BorderPolicy { get; }

    public int SourceSampleOffsetX { get; }

    public int SourceSampleOffsetZ { get; }

    public ReadOnlyMemory<ushort> Heights => m_Heights;

    public ReadOnlyMemory<byte> LayerWeights => m_LayerWeights;

    public IReadOnlyList<TerrainGeometricErrorLevel> GeometricErrors => m_GeometricErrors;

    public ushort GetHeightSample(int x, int z)
    {
        ValidateSampleCoordinate(x, z);
        return m_Heights[checked((z * Resolution) + x)];
    }

    public byte GetLayerWeight(int x, int z, int channel)
    {
        ValidateSampleCoordinate(x, z);
        if ((uint)channel >= TerrainCookedFormat.WeightChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        int sampleIndex = checked((z * Resolution) + x);
        return m_LayerWeights[checked((sampleIndex * TerrainCookedFormat.WeightChannelCount) + channel)];
    }

    public double DecodeHeight(ushort quantizedHeight)
    {
        return HeightRange.Min +
               ((double)quantizedHeight / ushort.MaxValue * HeightRange.Scale);
    }

    private void ValidateSampleCoordinate(int x, int z)
    {
        if ((uint)x >= (uint)Resolution || (uint)z >= (uint)Resolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Terrain tile sample ({x}, {z}) is outside {Resolution}x{Resolution}.");
        }
    }
}

public static class TerrainCookedFormat
{
    public const int WeightChannelCount = 4;
}

public static class TerrainTextureCookVariants
{
    public const string Albedo = "r8g8b8a8unorm.srgb.mips";
    public const string Normal = "r8g8b8a8unorm.linear.mips.normalmap";
    public const string Orm = "r8g8b8a8unorm.linear.mips";
}
