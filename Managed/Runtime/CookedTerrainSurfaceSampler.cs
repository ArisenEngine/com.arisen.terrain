using System.Numerics;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Terrain;

public readonly struct CookedTerrainSurfaceSample
{
    internal CookedTerrainSurfaceSample(
        Guid rootGuid,
        Guid tileGuid,
        TerrainTileCoordinate coordinate,
        in WorldPosition surfacePosition,
        in Vector3 normal,
        in Vector4 layerWeights)
    {
        RootGuid = rootGuid;
        TileGuid = tileGuid;
        Coordinate = coordinate;
        SurfacePosition = surfacePosition;
        Normal = normal;
        LayerWeights = layerWeights;
    }

    public Guid RootGuid { get; }

    public Guid TileGuid { get; }

    public TerrainTileCoordinate Coordinate { get; }

    public WorldPosition SurfacePosition { get; }

    public Vector3 Normal { get; }

    public Vector4 LayerWeights { get; }
}

internal readonly struct TerrainTileAxisResolver
{
    private readonly double m_Origin;
    private readonly double m_Spacing;
    private readonly int m_Intervals;
    private readonly int m_TileCount;
    private readonly double m_EstimatedTileSize;

    public TerrainTileAxisResolver(
        Guid rootGuid,
        string axis,
        double origin,
        double spacing,
        int intervals,
        int tileCount)
    {
        if (!double.IsFinite(origin) ||
            !double.IsFinite(spacing) ||
            spacing <= 0.0 ||
            intervals <= 0 ||
            tileCount <= 0)
        {
            throw new InvalidOperationException(
                $"Terrain root '{rootGuid:D}' has invalid {axis}-axis tile coverage.");
        }

        m_Origin = origin;
        m_Spacing = spacing;
        m_Intervals = intervals;
        m_TileCount = tileCount;
        m_EstimatedTileSize = intervals * spacing;
        Maximum = 0.0;
        if (!double.IsFinite(m_EstimatedTileSize) || m_EstimatedTileSize <= 0.0)
        {
            throw new InvalidOperationException(
                $"Terrain root '{rootGuid:D}' has invalid {axis}-axis tile size.");
        }

        double previous = GetBoundary(0);
        for (int index = 1; index <= tileCount; index++)
        {
            double boundary = GetBoundary(index);
            if (!double.IsFinite(boundary) || boundary <= previous)
            {
                throw new InvalidOperationException(
                    $"Terrain root '{rootGuid:D}' has collapsed or non-finite {axis}-axis " +
                    $"boundary '{index}'.");
            }

            previous = boundary;
        }

        Maximum = previous;
    }

    public double Maximum { get; }

    public double GetBoundary(int localTileIndex)
    {
        if ((uint)localTileIndex > (uint)m_TileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localTileIndex));
        }

        long sourceSampleOffset = checked((long)localTileIndex * m_Intervals);
        return m_Origin + (sourceSampleOffset * m_Spacing);
    }

    public int Resolve(double worldCoordinate)
    {
        int localTile = Math.Clamp(
            (int)Math.Floor((worldCoordinate - m_Origin) / m_EstimatedTileSize),
            0,
            m_TileCount - 1);
        while (localTile + 1 < m_TileCount &&
               worldCoordinate >= GetBoundary(localTile + 1))
        {
            localTile++;
        }

        while (localTile > 0 && worldCoordinate < GetBoundary(localTile))
        {
            localTile--;
        }

        return localTile;
    }
}

internal static class TerrainSurfaceSamplingDomain
{
    public static void Validate(CookedTerrainRoot root)
    {
        double minimumWorldY = root.WorldPlacement.Y + root.HeightRange.Min;
        double maximumWorldY = root.WorldPlacement.Y + root.HeightRange.Max;
        double maximumHeightDelta = root.HeightRange.Scale;
        double maximumGradientX = maximumHeightDelta / root.SampleSpacing.X;
        double maximumGradientZ = maximumHeightDelta / root.SampleSpacing.Z;
        if (!double.IsFinite(minimumWorldY) ||
            !double.IsFinite(maximumWorldY) ||
            !double.IsFinite(maximumGradientX) ||
            !double.IsFinite(maximumGradientZ))
        {
            throw new InvalidOperationException(
                $"Terrain root '{root.Guid:D}' has a non-finite surface sampling domain.");
        }
    }
}

public sealed class CookedTerrainSurfaceSampler
{
    private readonly CookedTerrainRoot m_Root;
    private readonly CookedTerrainTile[] m_Tiles;
    private readonly int m_TileCountX;
    private readonly int m_TileCountZ;
    private readonly TerrainTileAxisResolver m_XAxis;
    private readonly TerrainTileAxisResolver m_ZAxis;
    private readonly double m_MaxX;
    private readonly double m_MaxZ;

    public CookedTerrainSurfaceSampler(
        CookedTerrainRoot root,
        IReadOnlyList<CookedTerrainTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tiles);

        ValidateRoot(
            root,
            out m_TileCountX,
            out m_TileCountZ,
            out m_XAxis,
            out m_ZAxis,
            out m_MaxX,
            out m_MaxZ);
        if (tiles.Count != root.Tiles.Count)
        {
            throw Invalid(
                root,
                $"tile set count '{tiles.Count}' does not match root count '{root.Tiles.Count}'");
        }

        m_Root = root;
        m_Tiles = new CookedTerrainTile[root.Tiles.Count];
        var references = new CookedTerrainTileReference[root.Tiles.Count];
        for (int index = 0; index < root.Tiles.Count; index++)
        {
            CookedTerrainTileReference reference = root.Tiles[index]
                ?? throw Invalid(root, $"tile reference '{index}' is null");
            int denseIndex = GetDenseIndex(root, reference.Coordinate, m_TileCountX, m_TileCountZ);
            if (references[denseIndex] != null)
            {
                throw Invalid(root, $"repeats tile coordinate {reference.Coordinate}");
            }

            ValidateReference(root, reference, denseIndex, m_TileCountX, m_TileCountZ);
            references[denseIndex] = reference;
        }

        for (int index = 0; index < tiles.Count; index++)
        {
            CookedTerrainTile tile = tiles[index]
                ?? throw Invalid(root, $"tile set entry '{index}' is null");
            int denseIndex = GetDenseIndex(root, tile.Coordinate, m_TileCountX, m_TileCountZ);
            if (m_Tiles[denseIndex] != null)
            {
                throw Invalid(root, $"tile set repeats coordinate {tile.Coordinate}");
            }

            CookedTerrainTileReference reference = references[denseIndex]
                ?? throw Invalid(root, $"does not reference coordinate {tile.Coordinate}");
            ValidateTile(root, reference, tile, denseIndex, m_TileCountX);
            m_Tiles[denseIndex] = tile;
        }

        for (int index = 0; index < m_Tiles.Length; index++)
        {
            if (references[index] == null || m_Tiles[index] == null)
            {
                throw Invalid(root, $"tile set is incomplete at grid index '{index}'");
            }
        }

        ValidateSharedBorders(root, m_Tiles, m_TileCountX, m_TileCountZ);
    }

    public bool TrySample(
        WorldPosition worldPosition,
        out CookedTerrainSurfaceSample sample)
    {
        sample = default;
        if (!worldPosition.IsFinite ||
            worldPosition.X < m_Root.WorldPlacement.X || worldPosition.X > m_MaxX ||
            worldPosition.Z < m_Root.WorldPlacement.Z || worldPosition.Z > m_MaxZ)
        {
            return false;
        }

        int localTileX = m_XAxis.Resolve(worldPosition.X);
        int localTileZ = m_ZAxis.Resolve(worldPosition.Z);
        CookedTerrainTile tile = m_Tiles[(localTileZ * m_TileCountX) + localTileX];
        TerrainTileSurfaceSampling.Sample(
            tile,
            worldPosition,
            out WorldPosition surfacePosition,
            out Vector3 normal,
            out Vector4 layerWeights);
        sample = new CookedTerrainSurfaceSample(
            m_Root.Guid,
            tile.Guid,
            tile.Coordinate,
            surfacePosition,
            normal,
            layerWeights);
        return true;
    }

    private static void ValidateRoot(
        CookedTerrainRoot root,
        out int tileCountX,
        out int tileCountZ,
        out TerrainTileAxisResolver xAxis,
        out TerrainTileAxisResolver zAxis,
        out double maxX,
        out double maxZ)
    {
        if (root.Guid == Guid.Empty ||
            root.LayerSetGuid == Guid.Empty ||
            root.SourceSchemaVersion is < TerrainRootSourceAssetLoader.MinimumSourceSchemaVersion or
                > TerrainRootSourceAssetLoader.CurrentSourceSchemaVersion ||
            string.IsNullOrWhiteSpace(root.PackageId) ||
            string.IsNullOrWhiteSpace(root.LayerSetPackageId) ||
            !root.WorldPlacement.IsFinite ||
            !root.SampleSpacing.IsValid ||
            !root.HeightRange.IsValid ||
            root.BorderPolicy != TerrainBorderPolicy.SharedEdgeSamples ||
            root.Layers is null || root.Layers.Count is < 1 or > TerrainCookedFormat.WeightChannelCount ||
            root.Tiles is null || root.Tiles.Count is <= 0 or > TerrainTileIdentity.MaxTileCount)
        {
            throw Invalid(root, "has invalid identity or sampling metadata");
        }

        int intervals = root.TileResolution - 1;
        if (root.TileResolution < TerrainRootSourceAssetLoader.MinTileResolution ||
            root.TileResolution > TerrainRootSourceAssetLoader.MaxTileResolution ||
            (intervals & (intervals - 1)) != 0 ||
            root.HeightSourceWidth < root.TileResolution ||
            root.HeightSourceHeight < root.TileResolution ||
            root.HeightSourceWidth > TerrainHeightSourceDecoder.MaxDimension ||
            root.HeightSourceHeight > TerrainHeightSourceDecoder.MaxDimension ||
            (root.HeightSourceWidth - 1) % intervals != 0 ||
            (root.HeightSourceHeight - 1) % intervals != 0)
        {
            throw Invalid(root, "has invalid source dimensions or tile resolution");
        }

        tileCountX = (root.HeightSourceWidth - 1) / intervals;
        tileCountZ = (root.HeightSourceHeight - 1) / intervals;
        int expectedTileCount;
        try
        {
            expectedTileCount = checked(tileCountX * tileCountZ);
            TerrainTileIdentity.ValidateCoordinate(root.TileOrigin);
            TerrainTileIdentity.ValidateCoordinate(new TerrainTileCoordinate(
                checked(root.TileOrigin.X + tileCountX - 1),
                checked(root.TileOrigin.Z + tileCountZ - 1)));
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException)
        {
            throw Invalid(root, "has invalid tile-grid coordinates", error);
        }

        if (expectedTileCount != root.Tiles.Count)
        {
            throw Invalid(
                root,
                $"tile count '{root.Tiles.Count}' does not match source grid '{expectedTileCount}'");
        }

        xAxis = new TerrainTileAxisResolver(
            root.Guid,
            "X",
            root.WorldPlacement.X,
            root.SampleSpacing.X,
            intervals,
            tileCountX);
        zAxis = new TerrainTileAxisResolver(
            root.Guid,
            "Z",
            root.WorldPlacement.Z,
            root.SampleSpacing.Z,
            intervals,
            tileCountZ);
        maxX = xAxis.Maximum;
        maxZ = zAxis.Maximum;
        TerrainSurfaceSamplingDomain.Validate(root);
    }

    private static void ValidateReference(
        CookedTerrainRoot root,
        CookedTerrainTileReference reference,
        int denseIndex,
        int tileCountX,
        int tileCountZ)
    {
        int localX = denseIndex % tileCountX;
        int localZ = denseIndex / tileCountX;
        Guid expectedGuid = TerrainTileIdentity.CreateGuid(
            root.Guid,
            root.PackageId,
            reference.Coordinate);
        var expectedNeighbors = new TerrainTileNeighborSet(
            localX > 0
                ? TerrainTileIdentity.CreateGuid(
                    root.Guid,
                    root.PackageId,
                    new TerrainTileCoordinate(reference.Coordinate.X - 1, reference.Coordinate.Z))
                : Guid.Empty,
            localX + 1 < tileCountX
                ? TerrainTileIdentity.CreateGuid(
                    root.Guid,
                    root.PackageId,
                    new TerrainTileCoordinate(reference.Coordinate.X + 1, reference.Coordinate.Z))
                : Guid.Empty,
            localZ > 0
                ? TerrainTileIdentity.CreateGuid(
                    root.Guid,
                    root.PackageId,
                    new TerrainTileCoordinate(reference.Coordinate.X, reference.Coordinate.Z - 1))
                : Guid.Empty,
            localZ + 1 < tileCountZ
                ? TerrainTileIdentity.CreateGuid(
                    root.Guid,
                    root.PackageId,
                    new TerrainTileCoordinate(reference.Coordinate.X, reference.Coordinate.Z + 1))
                : Guid.Empty);
        if (reference.Guid != expectedGuid ||
            reference.Neighbors != expectedNeighbors ||
            !double.IsFinite(reference.MinHeight) ||
            !double.IsFinite(reference.MaxHeight) ||
            reference.MinHeight > reference.MaxHeight ||
            reference.MinHeight < root.HeightRange.Min ||
            reference.MaxHeight > root.HeightRange.Max ||
            reference.PayloadBytes < TerrainTileAssetCooker.HeaderSize ||
            reference.PayloadBytes > TerrainTileAssetCooker.MaxCookedTileBytes ||
            reference.ContentHash is null ||
            reference.ContentHash.Length != TerrainCookedContainer.HashSize ||
            IsAllZero(reference.ContentHash))
        {
            throw Invalid(root, $"tile reference {reference.Coordinate} is invalid");
        }
    }

    private static void ValidateTile(
        CookedTerrainRoot root,
        CookedTerrainTileReference reference,
        CookedTerrainTile tile,
        int denseIndex,
        int tileCountX)
    {
        int intervals = root.TileResolution - 1;
        int localX = denseIndex % tileCountX;
        int localZ = denseIndex / tileCountX;
        long sourceSampleOffsetX = checked((long)localX * intervals);
        long sourceSampleOffsetZ = checked((long)localZ * intervals);
        var expectedPlacement = new WorldPosition(
            root.WorldPlacement.X + (sourceSampleOffsetX * root.SampleSpacing.X),
            root.WorldPlacement.Y,
            root.WorldPlacement.Z + (sourceSampleOffsetZ * root.SampleSpacing.Z));
        int sampleCount = checked(root.TileResolution * root.TileResolution);
        if (tile.Guid != reference.Guid ||
            tile.RootGuid != root.Guid ||
            tile.LayerSetGuid != root.LayerSetGuid ||
            tile.PackageId != root.PackageId ||
            tile.SourceSchemaVersion != root.SourceSchemaVersion ||
            tile.Coordinate != reference.Coordinate ||
            tile.Resolution != root.TileResolution ||
            tile.LayerCount != root.Layers.Count ||
            tile.WorldPlacement != expectedPlacement ||
            tile.SampleSpacing != root.SampleSpacing ||
            tile.HeightRange != root.HeightRange ||
            tile.MinHeight != reference.MinHeight ||
            tile.MaxHeight != reference.MaxHeight ||
            tile.BorderPolicy != root.BorderPolicy ||
            tile.SourceSampleOffsetX != localX * intervals ||
            tile.SourceSampleOffsetZ != localZ * intervals ||
            tile.Heights.Length != sampleCount ||
            tile.LayerWeights.Length != checked(sampleCount * TerrainCookedFormat.WeightChannelCount))
        {
            throw Invalid(root, $"tile '{tile.Guid:D}' does not match reference {reference.Coordinate}");
        }
    }

    private static void ValidateSharedBorders(
        CookedTerrainRoot root,
        IReadOnlyList<CookedTerrainTile> tiles,
        int tileCountX,
        int tileCountZ)
    {
        for (int localZ = 0; localZ < tileCountZ; localZ++)
        {
            for (int localX = 0; localX < tileCountX; localX++)
            {
                int index = (localZ * tileCountX) + localX;
                if (localX + 1 < tileCountX)
                {
                    ValidateSharedBorder(root, tiles[index], tiles[index + 1], alongX: true);
                }

                if (localZ + 1 < tileCountZ)
                {
                    ValidateSharedBorder(
                        root,
                        tiles[index],
                        tiles[index + tileCountX],
                        alongX: false);
                }
            }
        }
    }

    private static void ValidateSharedBorder(
        CookedTerrainRoot root,
        CookedTerrainTile negative,
        CookedTerrainTile positive,
        bool alongX)
    {
        for (int sample = 0; sample < negative.Resolution; sample++)
        {
            ushort negativeHeight = alongX
                ? negative.GetHeightSample(negative.Resolution - 1, sample)
                : negative.GetHeightSample(sample, negative.Resolution - 1);
            ushort positiveHeight = alongX
                ? positive.GetHeightSample(0, sample)
                : positive.GetHeightSample(sample, 0);
            if (negativeHeight != positiveHeight)
            {
                throw Invalid(
                    root,
                    $"adjacent tiles {negative.Coordinate} and {positive.Coordinate} disagree at shared-border sample '{sample}'");
            }

            for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
            {
                byte negativeWeight = alongX
                    ? negative.GetLayerWeight(negative.Resolution - 1, sample, channel)
                    : negative.GetLayerWeight(sample, negative.Resolution - 1, channel);
                byte positiveWeight = alongX
                    ? positive.GetLayerWeight(0, sample, channel)
                    : positive.GetLayerWeight(sample, 0, channel);
                if (negativeWeight != positiveWeight)
                {
                    throw Invalid(
                        root,
                        $"adjacent tiles {negative.Coordinate} and {positive.Coordinate} disagree at shared-border weight sample '{sample}', channel '{channel}'");
                }
            }
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int GetDenseIndex(
        CookedTerrainRoot root,
        TerrainTileCoordinate coordinate,
        int tileCountX,
        int tileCountZ)
    {
        long localX = (long)coordinate.X - root.TileOrigin.X;
        long localZ = (long)coordinate.Z - root.TileOrigin.Z;
        if ((ulong)localX >= (ulong)tileCountX || (ulong)localZ >= (ulong)tileCountZ)
        {
            throw Invalid(root, $"tile coordinate {coordinate} is outside the root grid");
        }

        return checked(((int)localZ * tileCountX) + (int)localX);
    }

    private static InvalidOperationException Invalid(
        CookedTerrainRoot root,
        string diagnostic,
        Exception? inner = null) => new(
            $"Cooked terrain root '{root.Guid:D}' {diagnostic}.",
            inner);
}

internal static class TerrainTileSurfaceSampling
{
    public static void Sample(
        CookedTerrainTile tile,
        in WorldPosition worldPosition,
        out WorldPosition surfacePosition,
        out Vector3 normal,
        out Vector4 layerWeights)
    {
        double sampleX = Math.Clamp(
            (worldPosition.X - tile.WorldPlacement.X) / tile.SampleSpacing.X,
            0.0,
            tile.Resolution - 1.0);
        double sampleZ = Math.Clamp(
            (worldPosition.Z - tile.WorldPlacement.Z) / tile.SampleSpacing.Z,
            0.0,
            tile.Resolution - 1.0);
        int x0 = Math.Min(tile.Resolution - 2, (int)Math.Floor(sampleX));
        int z0 = Math.Min(tile.Resolution - 2, (int)Math.Floor(sampleZ));
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        double tx = sampleX - x0;
        double tz = sampleZ - z0;

        double h00 = tile.DecodeHeight(tile.GetHeightSample(x0, z0));
        double h10 = tile.DecodeHeight(tile.GetHeightSample(x1, z0));
        double h01 = tile.DecodeHeight(tile.GetHeightSample(x0, z1));
        double h11 = tile.DecodeHeight(tile.GetHeightSample(x1, z1));
        double localHeight = Lerp(Lerp(h00, h10, tx), Lerp(h01, h11, tx), tz);
        double gradientX = Lerp(h10 - h00, h11 - h01, tz) / tile.SampleSpacing.X;
        double gradientZ = Lerp(h01 - h00, h11 - h10, tx) / tile.SampleSpacing.Z;
        normal = Normalize(-gradientX, 1.0, -gradientZ);
        layerWeights = SampleWeights(tile, x0, z0, x1, z1, tx, tz);
        double surfaceY = tile.WorldPlacement.Y + localHeight;
        if (!double.IsFinite(surfaceY))
        {
            throw new InvalidOperationException(
                $"Terrain tile '{tile.Guid:D}' produced a non-finite surface height.");
        }

        surfacePosition = new WorldPosition(
            worldPosition.X,
            surfaceY,
            worldPosition.Z);
    }

    private static Vector4 SampleWeights(
        CookedTerrainTile tile,
        int x0,
        int z0,
        int x1,
        int z1,
        double tx,
        double tz)
    {
        Span<double> weights = stackalloc double[TerrainCookedFormat.WeightChannelCount];
        double sum = 0.0;
        for (int channel = 0; channel < TerrainCookedFormat.WeightChannelCount; channel++)
        {
            double w00 = tile.GetLayerWeight(x0, z0, channel);
            double w10 = tile.GetLayerWeight(x1, z0, channel);
            double w01 = tile.GetLayerWeight(x0, z1, channel);
            double w11 = tile.GetLayerWeight(x1, z1, channel);
            double value = Lerp(Lerp(w00, w10, tx), Lerp(w01, w11, tx), tz);
            weights[channel] = value;
            sum += value;
        }

        if (!double.IsFinite(sum) || sum <= 0.0)
        {
            return new Vector4(1.0f, 0.0f, 0.0f, 0.0f);
        }

        double inverse = 1.0 / sum;
        return new Vector4(
            (float)(weights[0] * inverse),
            (float)(weights[1] * inverse),
            (float)(weights[2] * inverse),
            (float)(weights[3] * inverse));
    }

    private static Vector3 Normalize(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new InvalidOperationException(
                "Terrain surface gradient contains a non-finite component.");
        }

        double maximum = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        if (maximum <= double.Epsilon)
        {
            return Vector3.UnitY;
        }

        double scaledX = x / maximum;
        double scaledY = y / maximum;
        double scaledZ = z / maximum;
        double inverseLength = 1.0 / Math.Sqrt(
            (scaledX * scaledX) +
            (scaledY * scaledY) +
            (scaledZ * scaledZ));
        return new Vector3(
            (float)(scaledX * inverseLength),
            (float)(scaledY * inverseLength),
            (float)(scaledZ * inverseLength));
    }

    private static double Lerp(double left, double right, double amount)
    {
        double delta = right - left;
        return double.IsFinite(delta)
            ? left + (delta * amount)
            : (left * (1.0 - amount)) + (right * amount);
    }
}
