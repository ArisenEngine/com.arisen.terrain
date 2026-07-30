using System.Globalization;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain.Assets;

public static class TerrainAssetTypes
{
    public const string Root = "TerrainRoot";
    public const string LayerSet = "TerrainLayerSet";
    public const string Tile = "TerrainTile";
}

public sealed class TerrainRootSourceAsset
{
    private TerrainRootSourceAsset() { }
}

public sealed class TerrainLayerSetSourceAsset
{
    private TerrainLayerSetSourceAsset() { }
}

public sealed class TerrainTileSourceAsset
{
    private TerrainTileSourceAsset() { }
}

public readonly record struct TerrainTileCoordinate(int X, int Z) : IComparable<TerrainTileCoordinate>
{
    public int CompareTo(TerrainTileCoordinate other)
    {
        int result = Z.CompareTo(other.Z);
        return result != 0 ? result : X.CompareTo(other.X);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"({X}, {Z})");
}

public static class TerrainTileIdentity
{
    public const string ChildKind = "terrain-tile";
    public const string Importer = "ArisenTerrainTileImporter";
    public const int MaxCoordinateMagnitude = 1_000_000;
    public const int MaxTileCount = 65_536;

    public static string CreateChildKey(TerrainTileCoordinate coordinate)
    {
        ValidateCoordinate(coordinate);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"x={coordinate.X};z={coordinate.Z}");
    }

    public static Guid CreateGuid(
        Guid terrainRootGuid,
        string sourcePackageId,
        TerrainTileCoordinate coordinate)
    {
        return GeneratedAssetIdentity.CreateChildGuid(
            terrainRootGuid,
            sourcePackageId,
            ChildKind,
            CreateChildKey(coordinate));
    }

    public static AssetMetadata CreateMetadata(
        Guid terrainRootGuid,
        string sourcePackageId,
        TerrainTileCoordinate coordinate)
    {
        return GeneratedAssetIdentity.CreateChildMetadata(
            terrainRootGuid,
            sourcePackageId,
            ChildKind,
            CreateChildKey(coordinate),
            TerrainAssetTypes.Tile,
            Importer);
    }

    public static TerrainGeneratedTileRecord[] CreateRecords(
        Guid terrainRootGuid,
        string sourcePackageId,
        TerrainTileCoordinate tileOrigin,
        int tileCountX,
        int tileCountZ)
    {
        ValidateCoordinate(tileOrigin);
        if (tileCountX <= 0 || tileCountZ <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileCountX),
                "Terrain tile dimensions must both be positive.");
        }

        int tileCount = checked(tileCountX * tileCountZ);
        if (tileCount > MaxTileCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileCountX),
                $"Terrain tile count '{tileCount}' exceeds {MaxTileCount}.");
        }

        var records = new TerrainGeneratedTileRecord[tileCount];
        int index = 0;
        for (int z = 0; z < tileCountZ; z++)
        {
            for (int x = 0; x < tileCountX; x++)
            {
                var coordinate = new TerrainTileCoordinate(
                    checked(tileOrigin.X + x),
                    checked(tileOrigin.Z + z));
                ValidateCoordinate(coordinate);
                records[index++] = new TerrainGeneratedTileRecord(
                    coordinate,
                    CreateGuid(terrainRootGuid, sourcePackageId, coordinate));
            }
        }

        return records;
    }

    public static void ValidateCoordinate(TerrainTileCoordinate coordinate)
    {
        if (Math.Abs((long)coordinate.X) > MaxCoordinateMagnitude ||
            Math.Abs((long)coordinate.Z) > MaxCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                $"Terrain tile coordinates must be within +/-{MaxCoordinateMagnitude}.");
        }
    }
}

public enum TerrainHeightSourceFormat
{
    Pgm16BigEndianScalar = 1
}

public enum TerrainBorderPolicy
{
    SharedEdgeSamples = 1
}

public readonly record struct TerrainSampleSpacing(double X, double Z)
{
    public bool IsValid => double.IsFinite(X) && X > 0.0 && double.IsFinite(Z) && Z > 0.0;
}

public readonly record struct TerrainHeightRange(double Min, double Max)
{
    public bool IsValid =>
        double.IsFinite(Min) &&
        double.IsFinite(Max) &&
        Min < Max &&
        double.IsFinite(Max - Min);

    public double Scale => Max - Min;
}

public sealed record TerrainHeightSourceDescriptor(
    string AuthoredPath,
    string ResolvedPath,
    TerrainHeightSourceFormat Format,
    int Width,
    int Height);

public sealed record TerrainWeightSourceDescriptor(
    string AuthoredPath,
    string ResolvedPath,
    TerrainWeightSourceFormat Format,
    int Width,
    int Height);

public readonly record struct TerrainGeneratedTileRecord(
    TerrainTileCoordinate Coordinate,
    Guid Guid);

public sealed record TerrainRootSourceDescriptor(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    WorldPosition WorldPlacement,
    TerrainSampleSpacing SampleSpacing,
    TerrainHeightRange HeightRange,
    TerrainHeightSourceDescriptor HeightSource,
    int TileResolution,
    TerrainBorderPolicy BorderPolicy,
    TerrainTileCoordinate TileOrigin,
    AssetRef<TerrainLayerSetSourceAsset> LayerSet,
    IReadOnlyList<TerrainGeneratedTileRecord> GeneratedTiles)
{
    public TerrainWeightSourceDescriptor? WeightSource { get; init; }
}

public sealed record TerrainLayerDescriptor(
    string Id,
    AssetRef<Texture2DSourceAsset> Albedo,
    AssetRef<Texture2DSourceAsset> Normal,
    AssetRef<Texture2DSourceAsset> Orm,
    TerrainLayerTint Tint,
    float RoughnessMultiplier,
    float MetallicMultiplier,
    float NormalStrength,
    TerrainLayerWorldTiling WorldTiling);

public readonly record struct TerrainLayerTint(float R, float G, float B, float A)
{
    public static TerrainLayerTint White { get; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    public bool IsValid =>
        IsUnit(R) && IsUnit(G) && IsUnit(B) && IsUnit(A);

    private static bool IsUnit(float value) =>
        float.IsFinite(value) && value is >= 0.0f and <= 1.0f;
}

public readonly record struct TerrainLayerWorldTiling(float X, float Z)
{
    public static TerrainLayerWorldTiling Default { get; } = new(2.0f, 2.0f);

    public bool IsValid =>
        IsValidAxis(X) && IsValidAxis(Z);

    private static bool IsValidAxis(float value) =>
        float.IsFinite(value) &&
        value >= TerrainLayerMaterialLimits.MinimumWorldTiling &&
        value <= TerrainLayerMaterialLimits.MaximumWorldTiling;
}

public static class TerrainLayerMaterialLimits
{
    public const float MinimumRoughnessMultiplier = 0.0f;
    public const float MaximumRoughnessMultiplier = 4.0f;
    public const float MinimumMetallicMultiplier = 0.0f;
    public const float MaximumMetallicMultiplier = 1.0f;
    public const float MinimumNormalStrength = 0.0f;
    public const float MaximumNormalStrength = 4.0f;
    public const float MinimumWorldTiling = 0.01f;
    public const float MaximumWorldTiling = 1_000_000.0f;

    public static bool IsValid(
        float roughnessMultiplier,
        float metallicMultiplier,
        float normalStrength) =>
        IsBounded(
            roughnessMultiplier,
            MinimumRoughnessMultiplier,
            MaximumRoughnessMultiplier) &&
        IsBounded(
            metallicMultiplier,
            MinimumMetallicMultiplier,
            MaximumMetallicMultiplier) &&
        IsBounded(
            normalStrength,
            MinimumNormalStrength,
            MaximumNormalStrength);

    private static bool IsBounded(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}

public sealed record TerrainLayerSetSourceDescriptor(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    IReadOnlyList<TerrainLayerDescriptor> Layers);
