using System.Security.Cryptography;
using ArisenEngine.Core.Assets;

namespace ArisenEngine.Terrain.Assets;

public readonly record struct TerrainHeightBrush(
    double WorldX,
    double WorldZ,
    double Radius,
    int QuantizedDelta);

public readonly record struct TerrainWeightBrush(
    double WorldX,
    double WorldZ,
    double Radius,
    int LayerIndex,
    byte Opacity);

public readonly record struct TerrainHeightSampleDelta(
    int SampleIndex,
    ushort Before,
    ushort After);

public readonly record struct TerrainWeightSampleDelta(
    int SampleIndex,
    uint Before,
    uint After);

public enum TerrainBrushEditKind
{
    Height = 1,
    Weight = 2
}

public sealed class TerrainBrushEdit
{
    private readonly TerrainHeightSampleDelta[] m_HeightDeltas;
    private readonly TerrainWeightSampleDelta[] m_WeightDeltas;
    private readonly TerrainTileCoordinate[] m_AffectedTiles;

    internal TerrainBrushEdit(
        Guid rootGuid,
        TerrainBrushEditKind kind,
        TerrainHeightSampleDelta[] heightDeltas,
        TerrainWeightSampleDelta[] weightDeltas,
        TerrainTileCoordinate[] affectedTiles)
    {
        RootGuid = rootGuid;
        Kind = kind;
        m_HeightDeltas = heightDeltas ?? throw new ArgumentNullException(nameof(heightDeltas));
        m_WeightDeltas = weightDeltas ?? throw new ArgumentNullException(nameof(weightDeltas));
        m_AffectedTiles = affectedTiles ?? throw new ArgumentNullException(nameof(affectedTiles));
    }

    public Guid RootGuid { get; }
    public TerrainBrushEditKind Kind { get; }
    public ReadOnlyMemory<TerrainHeightSampleDelta> HeightDeltas => m_HeightDeltas;
    public ReadOnlyMemory<TerrainWeightSampleDelta> WeightDeltas => m_WeightDeltas;
    public ReadOnlyMemory<TerrainTileCoordinate> AffectedTiles => m_AffectedTiles;
    public int ChangedSampleCount => Kind == TerrainBrushEditKind.Height
        ? m_HeightDeltas.Length
        : m_WeightDeltas.Length;
    public bool HasChanges => ChangedSampleCount != 0;
}

public sealed class TerrainAuthoringPreviewRevision
{
    private readonly CookedTerrainTile[] m_ChangedTiles;
    private readonly TerrainTileCoordinate[] m_AffectedTiles;

    internal TerrainAuthoringPreviewRevision(
        CookedTerrainRoot root,
        ulong revision,
        IReadOnlyList<CookedTerrainTile> changedTiles,
        IReadOnlyList<TerrainTileCoordinate> affectedTiles,
        int dirtyHeightSampleCount,
        int dirtyWeightSampleCount)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (revision == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Revision = revision;
        m_ChangedTiles = changedTiles?.ToArray()
            ?? throw new ArgumentNullException(nameof(changedTiles));
        m_AffectedTiles = affectedTiles?.ToArray()
            ?? throw new ArgumentNullException(nameof(affectedTiles));
        DirtyHeightSampleCount = dirtyHeightSampleCount;
        DirtyWeightSampleCount = dirtyWeightSampleCount;

        if (m_ChangedTiles.Length != m_AffectedTiles.Length ||
            m_ChangedTiles.Length > TerrainAuthoringLimits.MaximumAffectedTilesPerRevision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedTiles),
                "A terrain preview revision has an invalid changed-tile count.");
        }

        for (int index = 0; index < m_ChangedTiles.Length; index++)
        {
            CookedTerrainTile tile = m_ChangedTiles[index]
                ?? throw new ArgumentException(
                    "Terrain preview revisions cannot contain null tiles.",
                    nameof(changedTiles));
            if (tile.RootGuid != root.Guid ||
                tile.Coordinate != m_AffectedTiles[index] ||
                (index > 0 && m_AffectedTiles[index - 1].CompareTo(m_AffectedTiles[index]) >= 0))
            {
                throw new ArgumentException(
                    "Terrain preview tiles must match their root and use unique coordinate order.",
                    nameof(changedTiles));
            }
        }

        Root = CloneRoot(root);
        ChangedTiles = Array.AsReadOnly(m_ChangedTiles);
        AffectedTiles = Array.AsReadOnly(m_AffectedTiles);
    }

    public CookedTerrainRoot Root { get; }
    public Guid RootGuid => Root.Guid;
    public ulong Revision { get; }
    public IReadOnlyList<CookedTerrainTile> ChangedTiles { get; }
    public IReadOnlyList<TerrainTileCoordinate> AffectedTiles { get; }
    public int DirtyHeightSampleCount { get; }
    public int DirtyWeightSampleCount { get; }
    public bool IsDirty => DirtyHeightSampleCount != 0 || DirtyWeightSampleCount != 0;

    public bool TryGetChangedTile(Guid tileGuid, out CookedTerrainTile tile)
    {
        for (int index = 0; index < m_ChangedTiles.Length; index++)
        {
            if (m_ChangedTiles[index].Guid == tileGuid)
            {
                tile = m_ChangedTiles[index];
                return true;
            }
        }

        tile = null!;
        return false;
    }

    private static CookedTerrainRoot CloneRoot(CookedTerrainRoot root)
    {
        CookedTerrainLayer[] layers = root.Layers.ToArray();
        CookedTerrainTileReference[] tiles = root.Tiles
            .Select(tile => tile with { ContentHash = tile.ContentHash.ToArray() })
            .ToArray();
        return root with
        {
            Layers = Array.AsReadOnly(layers),
            Tiles = Array.AsReadOnly(tiles)
        };
    }
}

public static class TerrainAuthoringLimits
{
    public const int MaximumChangedSamplesPerBrush = 262_144;
    public const int MaximumAffectedTilesPerRevision = 256;
    public const int MaximumOpenPreviewRoots = 16;
    public const int MaximumPreviewDiagnosticLength = 1_024;
}

public sealed partial class TerrainAuthoringDocument
{
    private readonly TerrainRootSourceDescriptor m_SourceRoot;
    private readonly TerrainRootSourceDescriptor m_CookingRoot;
    private readonly TerrainLayerSetSourceDescriptor m_LayerSet;
    private readonly ushort[] m_OriginalHeights;
    private readonly ushort[] m_Heights;
    private readonly byte[] m_OriginalWeights;
    private readonly byte[] m_Weights;
    private readonly Dictionary<TerrainTileCoordinate, TerrainGeneratedTileRecord> m_TileRecords;
    private readonly Dictionary<TerrainTileCoordinate, CookedTerrainTileReference> m_TileReferences;
    private readonly TerrainTileCoordinate[] m_OrderedCoordinates;
    private readonly HashSet<int> m_DirtyHeightSamples = new();
    private readonly HashSet<int> m_DirtyWeightSamples = new();
    private CookedTerrainRoot m_CurrentRoot;
    private ulong m_Revision;

    internal TerrainAuthoringDocument(
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainHeightField heightField,
        TerrainWeightField? weightField,
        CookedTerrainRoot? baselineRoot = null)
    {
        m_SourceRoot = root ?? throw new ArgumentNullException(nameof(root));
        m_LayerSet = layerSet ?? throw new ArgumentNullException(nameof(layerSet));
        ArgumentNullException.ThrowIfNull(heightField);
        ValidateInputs(root, layerSet, heightField, weightField);

        m_Heights = heightField.Samples.ToArray();
        m_OriginalHeights = m_Heights.ToArray();
        m_Weights = BuildNormalizedWeights(
            heightField.Width,
            heightField.Height,
            layerSet.Layers.Count,
            weightField);
        m_OriginalWeights = m_Weights.ToArray();
        m_CookingRoot = root.WeightSource != null
            ? root
            : root with
            {
                WeightSource = new TerrainWeightSourceDescriptor(
                    "<authoring-preview>",
                    "<authoring-preview>",
                    TerrainWeightSourceFormat.Rgba8Hex,
                    heightField.Width,
                    heightField.Height)
            };

        m_TileRecords = root.GeneratedTiles.ToDictionary(record => record.Coordinate);
        m_OrderedCoordinates = root.GeneratedTiles
            .Select(record => record.Coordinate)
            .ToArray();
        m_CurrentRoot = BuildInitialRoot(baselineRoot);
        m_TileReferences = m_CurrentRoot.Tiles.ToDictionary(reference => reference.Coordinate);
    }

    public Guid RootGuid => m_SourceRoot.Guid;
    public int Width => m_SourceRoot.HeightSource.Width;
    public int Height => m_SourceRoot.HeightSource.Height;
    public int LayerCount => m_LayerSet.Layers.Count;
    public IReadOnlyList<TerrainLayerDescriptor> Layers => m_LayerSet.Layers;
    public ulong Revision => m_Revision;
    public int DirtyHeightSampleCount => m_DirtyHeightSamples.Count;
    public int DirtyWeightSampleCount => m_DirtyWeightSamples.Count;
    public bool IsDirty => DirtyHeightSampleCount != 0 || DirtyWeightSampleCount != 0;

    public static TerrainAuthoringDocument Load(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> rootRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!assetDatabase.CanReadSourceAssets)
        {
            throw new InvalidOperationException(
                "Terrain authoring requires an asset database with source access.");
        }

        if (!assetDatabase.TryGetAsset(rootRef.Guid, out AssetRecord? rootAsset) ||
            !string.Equals(rootAsset.AssetType, TerrainAssetTypes.Root, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(rootRef.PackageId) &&
             !string.Equals(rootAsset.PackageId, rootRef.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Terrain root '{rootRef.Guid:D}' is not an editable indexed source asset.");
        }

        TerrainRootSourceDescriptor root = TerrainRootSourceAssetLoader.LoadSource(rootAsset);
        TerrainLayerSetSourceDescriptor layerSet =
            TerrainLayerSetSourceAssetLoader.LoadSource(assetDatabase, root.LayerSet);
        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(
            root.HeightSource.ResolvedPath);
        TerrainWeightField? weightField = root.WeightSource == null
            ? null
            : TerrainWeightSourceDecoder.DecodeFile(root.WeightSource.ResolvedPath);
        CookedTerrainRoot? baseline = TerrainRootAssetCooker.TryLoadCooked(
            assetDatabase,
            rootRef,
            out CookedTerrainRoot cooked,
            out _)
            ? cooked
            : null;
        var document = new TerrainAuthoringDocument(
            root,
            layerSet,
            heightField,
            weightField,
            baseline);
        document.InitializeSourceBaseline(rootAsset);
        return document;
    }

    public TerrainBrushEdit CreateHeightBrushEdit(in TerrainHeightBrush brush)
    {
        ValidateBrush(brush.WorldX, brush.WorldZ, brush.Radius);
        if (Math.Abs((long)brush.QuantizedDelta) > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(brush),
                $"Quantized height delta must be within +/-{ushort.MaxValue}.");
        }
        if (brush.QuantizedDelta == 0)
        {
            return EmptyEdit(TerrainBrushEditKind.Height);
        }

        if (!TryGetSampleBounds(
                brush.WorldX,
                brush.WorldZ,
                brush.Radius,
                out int minX,
                out int maxX,
                out int minZ,
                out int maxZ))
        {
            return EmptyEdit(TerrainBrushEditKind.Height);
        }

        ValidateCandidateCount(minX, maxX, minZ, maxZ);
        var deltas = new List<TerrainHeightSampleDelta>();
        double radiusSquared = brush.Radius * brush.Radius;
        for (int z = minZ; z <= maxZ; z++)
        {
            double worldZ = m_SourceRoot.WorldPlacement.Z + (z * m_SourceRoot.SampleSpacing.Z);
            double deltaZ = worldZ - brush.WorldZ;
            for (int x = minX; x <= maxX; x++)
            {
                double worldX = m_SourceRoot.WorldPlacement.X + (x * m_SourceRoot.SampleSpacing.X);
                double deltaX = worldX - brush.WorldX;
                double distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                if (distanceSquared >= radiusSquared)
                {
                    continue;
                }

                double influence = 1.0 - (Math.Sqrt(distanceSquared) / brush.Radius);
                int appliedDelta = checked((int)Math.Round(
                    brush.QuantizedDelta * influence,
                    MidpointRounding.AwayFromZero));
                if (appliedDelta == 0)
                {
                    continue;
                }

                int sampleIndex = checked((z * Width) + x);
                ushort before = m_Heights[sampleIndex];
                ushort after = checked((ushort)Math.Clamp(
                    before + (long)appliedDelta,
                    ushort.MinValue,
                    ushort.MaxValue));
                if (after != before)
                {
                    deltas.Add(new TerrainHeightSampleDelta(sampleIndex, before, after));
                }
            }
        }

        return CreateEdit(TerrainBrushEditKind.Height, deltas.ToArray(), []);
    }

    public TerrainBrushEdit CreateWeightBrushEdit(in TerrainWeightBrush brush)
    {
        ValidateBrush(brush.WorldX, brush.WorldZ, brush.Radius);
        if ((uint)brush.LayerIndex >= (uint)LayerCount || brush.Opacity == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(brush),
                "Weight brush layer must be active and opacity must be non-zero.");
        }

        if (!TryGetSampleBounds(
                brush.WorldX,
                brush.WorldZ,
                brush.Radius,
                out int minX,
                out int maxX,
                out int minZ,
                out int maxZ))
        {
            return EmptyEdit(TerrainBrushEditKind.Weight);
        }

        ValidateCandidateCount(minX, maxX, minZ, maxZ);
        var deltas = new List<TerrainWeightSampleDelta>();
        double radiusSquared = brush.Radius * brush.Radius;
        Span<byte> painted = stackalloc byte[TerrainCookedFormat.WeightChannelCount];
        for (int z = minZ; z <= maxZ; z++)
        {
            double worldZ = m_SourceRoot.WorldPlacement.Z + (z * m_SourceRoot.SampleSpacing.Z);
            double deltaZ = worldZ - brush.WorldZ;
            for (int x = minX; x <= maxX; x++)
            {
                double worldX = m_SourceRoot.WorldPlacement.X + (x * m_SourceRoot.SampleSpacing.X);
                double deltaX = worldX - brush.WorldX;
                double distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                if (distanceSquared >= radiusSquared)
                {
                    continue;
                }

                double influence = 1.0 - (Math.Sqrt(distanceSquared) / brush.Radius);
                int opacity = checked((int)Math.Round(
                    brush.Opacity * influence,
                    MidpointRounding.AwayFromZero));
                if (opacity == 0)
                {
                    continue;
                }

                int sampleIndex = checked((z * Width) + x);
                int offset = sampleIndex * TerrainCookedFormat.WeightChannelCount;
                uint before = PackWeights(m_Weights.AsSpan(
                    offset,
                    TerrainCookedFormat.WeightChannelCount));
                PaintWeights(
                    m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount),
                    LayerCount,
                    brush.LayerIndex,
                    opacity,
                    painted);
                uint after = PackWeights(painted);
                if (after != before)
                {
                    deltas.Add(new TerrainWeightSampleDelta(sampleIndex, before, after));
                }
            }
        }

        return CreateEdit(TerrainBrushEditKind.Weight, [], deltas.ToArray());
    }

    public TerrainAuthoringPreviewRevision ApplyEdit(TerrainBrushEdit edit, bool useAfterValues)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.RootGuid != RootGuid || !edit.HasChanges)
        {
            throw new InvalidOperationException(
                "Terrain brush edit is empty or belongs to another authoring document.");
        }

        ValidateCurrentValues(edit, useAfterValues);
        ApplyValues(edit, useAfterValues);
        try
        {
            TerrainAuthoringPreviewRevision revision = BuildRevision(edit.AffectedTiles.Span);
            m_Revision = revision.Revision;
            return revision;
        }
        catch
        {
            ApplyValues(edit, !useAfterValues);
            throw;
        }
    }

    public ushort GetHeightSample(int x, int z)
    {
        ValidateSampleCoordinate(x, z);
        return m_Heights[checked((z * Width) + x)];
    }

    public uint GetPackedWeights(int x, int z)
    {
        ValidateSampleCoordinate(x, z);
        int offset = checked(((z * Width) + x) * TerrainCookedFormat.WeightChannelCount);
        return PackWeights(m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount));
    }

    private TerrainBrushEdit CreateEdit(
        TerrainBrushEditKind kind,
        TerrainHeightSampleDelta[] heightDeltas,
        TerrainWeightSampleDelta[] weightDeltas)
    {
        int changedCount = kind == TerrainBrushEditKind.Height
            ? heightDeltas.Length
            : weightDeltas.Length;
        if (changedCount > TerrainAuthoringLimits.MaximumChangedSamplesPerBrush)
        {
            throw new InvalidOperationException(
                $"Terrain brush changed {changedCount} samples, exceeding " +
                $"{TerrainAuthoringLimits.MaximumChangedSamplesPerBrush}.");
        }

        int[] sampleIndices = kind == TerrainBrushEditKind.Height
            ? heightDeltas.Select(delta => delta.SampleIndex).ToArray()
            : weightDeltas.Select(delta => delta.SampleIndex).ToArray();
        TerrainTileCoordinate[] affectedTiles = ResolveAffectedTiles(sampleIndices);
        if (affectedTiles.Length > TerrainAuthoringLimits.MaximumAffectedTilesPerRevision)
        {
            throw new InvalidOperationException(
                $"Terrain brush affects {affectedTiles.Length} tiles, exceeding " +
                $"{TerrainAuthoringLimits.MaximumAffectedTilesPerRevision}.");
        }

        return new TerrainBrushEdit(
            RootGuid,
            kind,
            heightDeltas,
            weightDeltas,
            affectedTiles);
    }

    private TerrainBrushEdit EmptyEdit(TerrainBrushEditKind kind) =>
        new(RootGuid, kind, [], [], []);

    private TerrainAuthoringPreviewRevision BuildRevision(
        ReadOnlySpan<TerrainTileCoordinate> affectedTiles,
        int? dirtyHeightSampleCount = null,
        int? dirtyWeightSampleCount = null)
    {
        TerrainTileCoordinate[] revisionTiles = affectedTiles
            .ToArray()
            .Concat(ResolveDirtyTiles())
            .Distinct()
            .Order()
            .ToArray();
        if (revisionTiles.Length > TerrainAuthoringLimits.MaximumAffectedTilesPerRevision)
        {
            throw new InvalidOperationException(
                $"Terrain authoring revision contains {revisionTiles.Length} dirty or " +
                $"restored tiles, exceeding {TerrainAuthoringLimits.MaximumAffectedTilesPerRevision}.");
        }

        var heightField = new TerrainHeightField(Width, Height, m_Heights);
        var weightField = new TerrainWeightField(Width, Height, m_Weights);
        var changedTiles = new CookedTerrainTile[revisionTiles.Length];
        var changedReferences = new CookedTerrainTileReference[revisionTiles.Length];
        for (int index = 0; index < revisionTiles.Length; index++)
        {
            TerrainTileCoordinate coordinate = revisionTiles[index];
            if (!m_TileRecords.TryGetValue(coordinate, out TerrainGeneratedTileRecord record))
            {
                throw new InvalidOperationException(
                    $"Terrain authoring tile {coordinate} is not part of root '{RootGuid:D}'.");
            }

            CookedTerrainTile tile = TerrainTileAssetCooker.BuildTile(
                m_CookingRoot,
                m_LayerSet,
                heightField,
                record,
                weightField);
            byte[] payload = TerrainTileAssetCooker.WritePayload(tile);
            changedTiles[index] = tile;
            CookedTerrainTileReference previous = m_TileReferences[coordinate];
            changedReferences[index] = previous with
            {
                MinHeight = tile.MinHeight,
                MaxHeight = tile.MaxHeight,
                PayloadBytes = payload.LongLength,
                ContentHash = SHA256.HashData(payload)
            };
        }

        TerrainTileAssetCooker.ValidateSharedBorders(changedTiles);
        var nextReferences = new Dictionary<TerrainTileCoordinate, CookedTerrainTileReference>(
            m_TileReferences);
        for (int index = 0; index < revisionTiles.Length; index++)
        {
            nextReferences[revisionTiles[index]] = changedReferences[index];
        }

        var orderedReferences = new CookedTerrainTileReference[m_OrderedCoordinates.Length];
        for (int index = 0; index < m_OrderedCoordinates.Length; index++)
        {
            orderedReferences[index] = nextReferences[m_OrderedCoordinates[index]];
        }

        CookedTerrainRoot nextRoot = m_CurrentRoot with { Tiles = orderedReferences };
        ulong nextRevision = checked(m_Revision + 1);
        var revision = new TerrainAuthoringPreviewRevision(
            nextRoot,
            nextRevision,
            changedTiles,
            revisionTiles,
            dirtyHeightSampleCount ?? DirtyHeightSampleCount,
            dirtyWeightSampleCount ?? DirtyWeightSampleCount);
        m_TileReferences.Clear();
        foreach ((TerrainTileCoordinate coordinate, CookedTerrainTileReference reference) in nextReferences)
        {
            m_TileReferences.Add(coordinate, reference);
        }
        m_CurrentRoot = nextRoot;
        return revision;
    }

    private CookedTerrainRoot BuildInitialRoot(CookedTerrainRoot? baselineRoot)
    {
        if (baselineRoot != null && IsCompatibleBaseline(baselineRoot))
        {
            CookedTerrainTileArtifact[] baselineArtifacts = baselineRoot.Tiles
                .Select(reference => new CookedTerrainTileArtifact(
                    reference.Guid,
                    baselineRoot.Guid,
                    reference.Coordinate,
                    TerrainTileAssetCooker.RuntimeVariant,
                    string.Empty,
                    reference.PayloadBytes,
                    reference.MinHeight,
                    reference.MaxHeight,
                    reference.ContentHash.ToArray()))
                .ToArray();
            return TerrainRootAssetCooker.BuildRoot(
                m_SourceRoot,
                m_LayerSet,
                baselineArtifacts);
        }

        var heightField = new TerrainHeightField(Width, Height, m_Heights);
        var weightField = new TerrainWeightField(Width, Height, m_Weights);
        var artifacts = new CookedTerrainTileArtifact[m_SourceRoot.GeneratedTiles.Count];
        for (int index = 0; index < m_SourceRoot.GeneratedTiles.Count; index++)
        {
            TerrainGeneratedTileRecord record = m_SourceRoot.GeneratedTiles[index];
            CookedTerrainTile tile = TerrainTileAssetCooker.BuildTile(
                m_CookingRoot,
                m_LayerSet,
                heightField,
                record,
                weightField);
            byte[] payload = TerrainTileAssetCooker.WritePayload(tile);
            artifacts[index] = new CookedTerrainTileArtifact(
                tile.Guid,
                tile.RootGuid,
                tile.Coordinate,
                TerrainTileAssetCooker.RuntimeVariant,
                string.Empty,
                payload.LongLength,
                tile.MinHeight,
                tile.MaxHeight,
                SHA256.HashData(payload));
        }

        return TerrainRootAssetCooker.BuildRoot(m_SourceRoot, m_LayerSet, artifacts);
    }

    private bool IsCompatibleBaseline(CookedTerrainRoot root)
    {
        if (root.Guid != RootGuid ||
            root.Tiles.Count != m_SourceRoot.GeneratedTiles.Count ||
            root.HeightSourceWidth != Width ||
            root.HeightSourceHeight != Height ||
            root.TileResolution != m_SourceRoot.TileResolution ||
            root.TileOrigin != m_SourceRoot.TileOrigin)
        {
            return false;
        }

        var references = root.Tiles.ToDictionary(reference => reference.Coordinate);
        for (int index = 0; index < m_SourceRoot.GeneratedTiles.Count; index++)
        {
            TerrainGeneratedTileRecord record = m_SourceRoot.GeneratedTiles[index];
            if (!references.TryGetValue(record.Coordinate, out CookedTerrainTileReference? reference) ||
                reference.Guid != record.Guid ||
                reference.ContentHash.Length != 32)
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateCurrentValues(TerrainBrushEdit edit, bool useAfterValues)
    {
        if (edit.Kind == TerrainBrushEditKind.Height)
        {
            ReadOnlySpan<TerrainHeightSampleDelta> deltas = edit.HeightDeltas.Span;
            for (int index = 0; index < deltas.Length; index++)
            {
                ref readonly TerrainHeightSampleDelta delta = ref deltas[index];
                ushort expected = useAfterValues ? delta.Before : delta.After;
                if ((uint)delta.SampleIndex >= (uint)m_Heights.Length ||
                    m_Heights[delta.SampleIndex] != expected)
                {
                    throw new InvalidOperationException(
                        "Terrain height edit no longer matches the authoring document state.");
                }
            }
            return;
        }

        ReadOnlySpan<TerrainWeightSampleDelta> weightDeltas = edit.WeightDeltas.Span;
        for (int index = 0; index < weightDeltas.Length; index++)
        {
            ref readonly TerrainWeightSampleDelta delta = ref weightDeltas[index];
            if ((uint)delta.SampleIndex >= (uint)m_Heights.Length)
            {
                throw new InvalidOperationException(
                    "Terrain weight edit contains an invalid sample index.");
            }

            int offset = delta.SampleIndex * TerrainCookedFormat.WeightChannelCount;
            uint expected = useAfterValues ? delta.Before : delta.After;
            if (PackWeights(m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount)) != expected)
            {
                throw new InvalidOperationException(
                    "Terrain weight edit no longer matches the authoring document state.");
            }
        }
    }

    private void ApplyValues(TerrainBrushEdit edit, bool useAfterValues)
    {
        if (edit.Kind == TerrainBrushEditKind.Height)
        {
            foreach (TerrainHeightSampleDelta delta in edit.HeightDeltas.Span)
            {
                m_Heights[delta.SampleIndex] = useAfterValues ? delta.After : delta.Before;
                UpdateHeightDirty(delta.SampleIndex);
            }
            return;
        }

        foreach (TerrainWeightSampleDelta delta in edit.WeightDeltas.Span)
        {
            int offset = delta.SampleIndex * TerrainCookedFormat.WeightChannelCount;
            UnpackWeights(
                useAfterValues ? delta.After : delta.Before,
                m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount));
            UpdateWeightDirty(delta.SampleIndex);
        }
    }

    private TerrainTileCoordinate[] ResolveAffectedTiles(ReadOnlySpan<int> sampleIndices)
    {
        var affected = new HashSet<TerrainTileCoordinate>();
        int intervals = m_SourceRoot.TileResolution - 1;
        int tileCountX = (Width - 1) / intervals;
        int tileCountZ = (Height - 1) / intervals;
        Span<int> ownersX = stackalloc int[2];
        Span<int> ownersZ = stackalloc int[2];
        for (int index = 0; index < sampleIndices.Length; index++)
        {
            int sampleIndex = sampleIndices[index];
            int sampleX = sampleIndex % Width;
            int sampleZ = sampleIndex / Width;
            int ownerCountX = ResolveAxisOwners(sampleX, intervals, tileCountX, ownersX);
            int ownerCountZ = ResolveAxisOwners(sampleZ, intervals, tileCountZ, ownersZ);
            for (int z = 0; z < ownerCountZ; z++)
            {
                for (int x = 0; x < ownerCountX; x++)
                {
                    affected.Add(new TerrainTileCoordinate(
                        checked(m_SourceRoot.TileOrigin.X + ownersX[x]),
                        checked(m_SourceRoot.TileOrigin.Z + ownersZ[z])));
                }
            }
        }

        TerrainTileCoordinate[] result = affected.ToArray();
        Array.Sort(result);
        return result;
    }

    private static int ResolveAxisOwners(
        int sample,
        int intervals,
        int tileCount,
        Span<int> owners)
    {
        int upper = sample / intervals;
        if (sample == tileCount * intervals)
        {
            owners[0] = tileCount - 1;
            return 1;
        }

        if (sample > 0 && sample % intervals == 0)
        {
            owners[0] = upper - 1;
            owners[1] = upper;
            return 2;
        }

        owners[0] = upper;
        return 1;
    }

    private bool TryGetSampleBounds(
        double worldX,
        double worldZ,
        double radius,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        double localMinX = (worldX - radius - m_SourceRoot.WorldPlacement.X) /
                           m_SourceRoot.SampleSpacing.X;
        double localMaxX = (worldX + radius - m_SourceRoot.WorldPlacement.X) /
                           m_SourceRoot.SampleSpacing.X;
        double localMinZ = (worldZ - radius - m_SourceRoot.WorldPlacement.Z) /
                           m_SourceRoot.SampleSpacing.Z;
        double localMaxZ = (worldZ + radius - m_SourceRoot.WorldPlacement.Z) /
                           m_SourceRoot.SampleSpacing.Z;
        if (localMaxX < 0.0 || localMaxZ < 0.0 ||
            localMinX > Width - 1 || localMinZ > Height - 1)
        {
            minX = maxX = minZ = maxZ = 0;
            return false;
        }

        minX = checked((int)Math.Max(0.0, Math.Ceiling(localMinX)));
        maxX = checked((int)Math.Min(Width - 1.0, Math.Floor(localMaxX)));
        minZ = checked((int)Math.Max(0.0, Math.Ceiling(localMinZ)));
        maxZ = checked((int)Math.Min(Height - 1.0, Math.Floor(localMaxZ)));
        return minX <= maxX && minZ <= maxZ;
    }

    private static void PaintWeights(
        ReadOnlySpan<byte> source,
        int layerCount,
        int targetLayer,
        int opacity,
        Span<byte> destination)
    {
        destination.Clear();
        if (layerCount == 1)
        {
            destination[0] = byte.MaxValue;
            return;
        }

        int oldTarget = source[targetLayer];
        int targetIncrease = ((byte.MaxValue - oldTarget) * opacity + 127) /
                             byte.MaxValue;
        int newTarget = Math.Min(byte.MaxValue, oldTarget + targetIncrease);
        destination[targetLayer] = checked((byte)newTarget);
        int remaining = byte.MaxValue - newTarget;
        int otherSum = 0;
        for (int channel = 0; channel < layerCount; channel++)
        {
            if (channel != targetLayer)
            {
                otherSum += source[channel];
            }
        }

        if (otherSum == 0)
        {
            for (int channel = 0; channel < layerCount; channel++)
            {
                if (channel != targetLayer)
                {
                    destination[channel] = checked((byte)remaining);
                    break;
                }
            }
            return;
        }

        Span<int> remainders = stackalloc int[TerrainCookedFormat.WeightChannelCount];
        int assigned = 0;
        for (int channel = 0; channel < layerCount; channel++)
        {
            if (channel == targetLayer)
            {
                remainders[channel] = -1;
                continue;
            }

            int scaled = source[channel] * remaining;
            int value = scaled / otherSum;
            destination[channel] = checked((byte)value);
            remainders[channel] = scaled % otherSum;
            assigned += value;
        }

        int unassigned = remaining - assigned;
        while (unassigned-- > 0)
        {
            int selected = -1;
            for (int channel = 0; channel < layerCount; channel++)
            {
                if (channel == targetLayer)
                {
                    continue;
                }

                if (selected < 0 || remainders[channel] > remainders[selected])
                {
                    selected = channel;
                }
            }

            destination[selected]++;
            remainders[selected] = -1;
        }
    }

    private static byte[] BuildNormalizedWeights(
        int width,
        int height,
        int layerCount,
        TerrainWeightField? weightField)
    {
        int sampleCount = checked(width * height);
        var weights = new byte[checked(sampleCount * TerrainCookedFormat.WeightChannelCount)];
        Span<byte> fallback = stackalloc byte[TerrainCookedFormat.WeightChannelCount];
        fallback[0] = byte.MaxValue;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            ReadOnlySpan<byte> source = weightField == null
                ? fallback
                : weightField.Weights.Span.Slice(
                    sample * TerrainCookedFormat.WeightChannelCount,
                    TerrainCookedFormat.WeightChannelCount);
            TerrainTileAssetCooker.NormalizeLayerWeights(
                source,
                layerCount,
                weights.AsSpan(
                    sample * TerrainCookedFormat.WeightChannelCount,
                    TerrainCookedFormat.WeightChannelCount),
                $"terrain authoring sample '{sample}'");
        }
        return weights;
    }

    private static uint PackWeights(ReadOnlySpan<byte> weights) =>
        (uint)(weights[0] |
               (weights[1] << 8) |
               (weights[2] << 16) |
               (weights[3] << 24));

    private static void UnpackWeights(uint packed, Span<byte> weights)
    {
        weights[0] = (byte)packed;
        weights[1] = (byte)(packed >> 8);
        weights[2] = (byte)(packed >> 16);
        weights[3] = (byte)(packed >> 24);
    }

    private void UpdateHeightDirty(int sampleIndex)
    {
        if (m_Heights[sampleIndex] == m_OriginalHeights[sampleIndex])
        {
            m_DirtyHeightSamples.Remove(sampleIndex);
        }
        else
        {
            m_DirtyHeightSamples.Add(sampleIndex);
        }
    }

    private void UpdateWeightDirty(int sampleIndex)
    {
        int offset = sampleIndex * TerrainCookedFormat.WeightChannelCount;
        bool changed = !m_Weights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount)
            .SequenceEqual(m_OriginalWeights.AsSpan(offset, TerrainCookedFormat.WeightChannelCount));
        if (changed)
        {
            m_DirtyWeightSamples.Add(sampleIndex);
        }
        else
        {
            m_DirtyWeightSamples.Remove(sampleIndex);
        }
    }

    private void ValidateCandidateCount(int minX, int maxX, int minZ, int maxZ)
    {
        long candidateCount = checked(
            ((long)maxX - minX + 1) * ((long)maxZ - minZ + 1));
        if (candidateCount > TerrainAuthoringLimits.MaximumChangedSamplesPerBrush)
        {
            throw new InvalidOperationException(
                $"Terrain brush candidate set '{candidateCount}' exceeds " +
                $"{TerrainAuthoringLimits.MaximumChangedSamplesPerBrush} samples.");
        }
    }

    private static void ValidateInputs(
        TerrainRootSourceDescriptor root,
        TerrainLayerSetSourceDescriptor layerSet,
        TerrainHeightField heightField,
        TerrainWeightField? weightField)
    {
        if (root.Guid == Guid.Empty ||
            layerSet.Guid != root.LayerSet.Guid ||
            !string.Equals(layerSet.PackageId, root.LayerSet.PackageId, StringComparison.Ordinal) ||
            layerSet.Layers.Count is < 1 or > TerrainCookedFormat.WeightChannelCount ||
            heightField.Width != root.HeightSource.Width ||
            heightField.Height != root.HeightSource.Height ||
            (weightField != null &&
             (weightField.Width != heightField.Width || weightField.Height != heightField.Height)))
        {
            throw new InvalidOperationException(
                "Terrain authoring inputs do not match the terrain root source contract.");
        }
    }

    private static void ValidateBrush(double worldX, double worldZ, double radius)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldZ) ||
            !double.IsFinite(radius) || radius <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                "Terrain brush coordinates and radius must be finite, with radius greater than zero.");
        }
    }

    private void ValidateSampleCoordinate(int x, int z)
    {
        if ((uint)x >= (uint)Width || (uint)z >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                $"Terrain authoring sample ({x}, {z}) is outside {Width}x{Height}.");
        }
    }
}
