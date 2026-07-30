using ArisenEngine.Core.Assets;

namespace ArisenEngine.Terrain.Assets;

[Flags]
public enum TerrainAuthoringExternalChanges
{
    None = 0,
    Root = 1 << 0,
    Height = 1 << 1,
    Weights = 1 << 2
}

public enum TerrainAuthoringReimportConflictResolution
{
    Cancel = 0,
    ReloadExternal = 1,
    MergeLocalChanges = 2
}

public sealed record TerrainAuthoringSourceSaveResult(
    bool Saved,
    TerrainAuthoringPreviewRevision? PreviewRevision,
    IReadOnlyList<TerrainTileCoordinate> ChangedTiles,
    IReadOnlyList<string> WrittenPaths);

public sealed record TerrainAuthoringSourceReimportResult(
    bool Reimported,
    bool HadLocalConflict,
    TerrainAuthoringReimportConflictResolution Resolution,
    TerrainAuthoringPreviewRevision? PreviewRevision,
    IReadOnlyList<TerrainTileCoordinate> ExternallyChangedTiles);

public sealed partial class TerrainAuthoringDocument
{
    private SourceBaseline? m_SourceBaseline;
    private TerrainAuthoringExternalChanges m_ExternalChanges;

    public AssetRef<TerrainRootSourceAsset> RootReference => new(
        RootGuid,
        TerrainAssetTypes.Root,
        m_SourceRoot.PackageId);

    public string RootSourcePath => m_SourceBaseline?.RootAsset.SourcePath ?? string.Empty;
    public TerrainAuthoringExternalChanges ExternalChanges => m_ExternalChanges;
    public bool HasExternalChanges => m_ExternalChanges != TerrainAuthoringExternalChanges.None;

    public bool ReferencesSourcePath(string? path)
    {
        if (m_SourceBaseline == null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        return PathsEqual(fullPath, m_SourceBaseline.RootAsset.SourcePath) ||
               PathsEqual(fullPath, m_SourceRoot.HeightSource.ResolvedPath) ||
               (m_SourceRoot.WeightSource != null &&
                PathsEqual(fullPath, m_SourceRoot.WeightSource.ResolvedPath));
    }

    public TerrainAuthoringExternalChanges RefreshExternalChanges()
    {
        if (m_SourceBaseline == null)
        {
            m_ExternalChanges = TerrainAuthoringExternalChanges.None;
            return m_ExternalChanges;
        }

        TerrainAuthoringExternalChanges changes = TerrainAuthoringExternalChanges.None;
        if (!FileMatches(
                m_SourceBaseline.RootAsset.SourcePath,
                m_SourceBaseline.RootBytes))
        {
            changes |= TerrainAuthoringExternalChanges.Root;
        }
        if (!FileMatches(
                m_SourceRoot.HeightSource.ResolvedPath,
                m_SourceBaseline.HeightBytes))
        {
            changes |= TerrainAuthoringExternalChanges.Height;
        }
        if (m_SourceRoot.WeightSource != null &&
            !FileMatches(
                m_SourceRoot.WeightSource.ResolvedPath,
                m_SourceBaseline.WeightBytes ?? []))
        {
            changes |= TerrainAuthoringExternalChanges.Weights;
        }

        m_ExternalChanges = changes;
        return changes;
    }

    public TerrainAuthoringSourceSaveResult SaveSources()
    {
        SourceBaseline baseline = RequireSourceBaseline();
        if (!IsDirty)
        {
            return new TerrainAuthoringSourceSaveResult(
                false,
                null,
                Array.Empty<TerrainTileCoordinate>(),
                Array.Empty<string>());
        }

        TerrainAuthoringExternalChanges external = RefreshExternalChanges();
        if (external != TerrainAuthoringExternalChanges.None)
        {
            throw new InvalidOperationException(
                $"Terrain source changed on disk ({external}). Save was blocked; " +
                "reimport with an explicit conflict resolution first.");
        }

        if (DirtyWeightSampleCount != 0 && m_SourceRoot.WeightSource == null)
        {
            throw new InvalidOperationException(
                "This terrain has no persisted WeightSource. Reimport it through the current " +
                "terrain creation workflow before saving painted weights.");
        }

        TerrainTileCoordinate[] changedTiles = ResolveDirtyTiles();
        ValidateTransactionTileCount(changedTiles, "save");
        var writes = new List<TerrainImportFileWrite>(2);
        byte[]? heightBytes = null;
        byte[]? weightBytes = null;
        if (DirtyHeightSampleCount != 0)
        {
            heightBytes = TerrainHeightSourceEncoder.Encode(Width, Height, m_Heights);
            writes.Add(new TerrainImportFileWrite(
                m_SourceRoot.HeightSource.ResolvedPath,
                heightBytes));
        }
        if (DirtyWeightSampleCount != 0)
        {
            weightBytes = TerrainWeightSourceEncoder.Encode(Width, Height, m_Weights);
            writes.Add(new TerrainImportFileWrite(
                m_SourceRoot.WeightSource!.ResolvedPath,
                weightBytes));
        }

        CookedTerrainRoot previousRoot = m_CurrentRoot;
        var previousReferences = new Dictionary<TerrainTileCoordinate, CookedTerrainTileReference>(
            m_TileReferences);
        TerrainAuthoringPreviewRevision cleanRevision;
        try
        {
            cleanRevision = BuildRevision(
                changedTiles,
                dirtyHeightSampleCount: 0,
                dirtyWeightSampleCount: 0);
            TerrainImportEmitter.ExecuteTransaction(
                baseline.AssetsRoot,
                writes,
                Array.Empty<string>());
        }
        catch
        {
            RestorePreviewState(previousRoot, previousReferences);
            throw;
        }

        Array.Copy(m_Heights, m_OriginalHeights, m_Heights.Length);
        Array.Copy(m_Weights, m_OriginalWeights, m_Weights.Length);
        m_DirtyHeightSamples.Clear();
        m_DirtyWeightSamples.Clear();
        m_Revision = cleanRevision.Revision;
        if (heightBytes != null)
        {
            baseline.HeightBytes = heightBytes;
        }
        if (weightBytes != null)
        {
            baseline.WeightBytes = weightBytes;
        }
        m_ExternalChanges = TerrainAuthoringExternalChanges.None;

        return new TerrainAuthoringSourceSaveResult(
            true,
            cleanRevision,
            Array.AsReadOnly(changedTiles),
            Array.AsReadOnly(writes.Select(write => Path.GetFullPath(write.Path)).ToArray()));
    }

    public TerrainAuthoringSourceReimportResult ReimportSources(
        TerrainAuthoringReimportConflictResolution resolution)
    {
        SourceBaseline baseline = RequireSourceBaseline();
        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        TerrainAuthoringExternalChanges external = RefreshExternalChanges();
        if ((external & TerrainAuthoringExternalChanges.Root) != 0)
        {
            throw new InvalidOperationException(
                "The terrain root descriptor changed on disk. Close and reopen the terrain " +
                "document before applying structural source changes.");
        }
        if (external == TerrainAuthoringExternalChanges.None)
        {
            return new TerrainAuthoringSourceReimportResult(
                false,
                false,
                resolution,
                null,
                Array.Empty<TerrainTileCoordinate>());
        }

        bool hadLocalConflict = IsDirty;
        if (hadLocalConflict && resolution == TerrainAuthoringReimportConflictResolution.Cancel)
        {
            throw new InvalidOperationException(
                "Terrain source changed on disk while the document has unsaved edits. " +
                "Choose Reload External or Merge Local Changes explicitly.");
        }

        byte[] diskHeightBytes = File.ReadAllBytes(m_SourceRoot.HeightSource.ResolvedPath);
        TerrainHeightField diskHeight = TerrainHeightSourceDecoder.Decode(
            diskHeightBytes,
            m_SourceRoot.HeightSource.ResolvedPath);
        if (diskHeight.Width != Width || diskHeight.Height != Height)
        {
            throw new InvalidDataException(
                $"External height dimensions {diskHeight.Width}x{diskHeight.Height} do not " +
                $"match the open terrain document {Width}x{Height}.");
        }

        byte[]? diskWeightBytes = null;
        TerrainWeightField? diskWeight = null;
        if (m_SourceRoot.WeightSource != null)
        {
            diskWeightBytes = File.ReadAllBytes(m_SourceRoot.WeightSource.ResolvedPath);
            diskWeight = TerrainWeightSourceDecoder.Decode(
                diskWeightBytes,
                m_SourceRoot.WeightSource.ResolvedPath);
            if (diskWeight.Width != Width || diskWeight.Height != Height)
            {
                throw new InvalidDataException(
                    $"External weight dimensions {diskWeight.Width}x{diskWeight.Height} do not " +
                    $"match the open terrain document {Width}x{Height}.");
            }
        }

        ushort[] externalHeights = diskHeight.Samples.ToArray();
        byte[] externalWeights = diskWeight == null
            ? m_OriginalWeights.ToArray()
            : BuildNormalizedWeights(
                Width,
                Height,
                LayerCount,
                diskWeight);
        int[] externalChangedSamples = CollectChangedSamples(
            m_OriginalHeights,
            externalHeights,
            m_OriginalWeights,
            externalWeights);
        TerrainTileCoordinate[] externallyChangedTiles = ResolveAffectedTiles(
            externalChangedSamples);
        ValidateTransactionTileCount(externallyChangedTiles, "external reimport");

        ushort[] previousWorkingHeights = m_Heights.ToArray();
        byte[] previousWorkingWeights = m_Weights.ToArray();
        ushort[] previousOriginalHeights = m_OriginalHeights.ToArray();
        byte[] previousOriginalWeights = m_OriginalWeights.ToArray();
        int[] previousDirtyHeight = m_DirtyHeightSamples.Order().ToArray();
        int[] previousDirtyWeight = m_DirtyWeightSamples.Order().ToArray();
        CookedTerrainRoot previousRoot = m_CurrentRoot;
        var previousReferences = new Dictionary<TerrainTileCoordinate, CookedTerrainTileReference>(
            m_TileReferences);
        ulong previousRevision = m_Revision;

        ushort[] nextHeights = externalHeights.ToArray();
        byte[] nextWeights = externalWeights.ToArray();
        if (hadLocalConflict &&
            resolution == TerrainAuthoringReimportConflictResolution.MergeLocalChanges)
        {
            foreach (int sampleIndex in previousDirtyHeight)
            {
                nextHeights[sampleIndex] = previousWorkingHeights[sampleIndex];
            }
            foreach (int sampleIndex in previousDirtyWeight)
            {
                int offset = sampleIndex * TerrainCookedFormat.WeightChannelCount;
                previousWorkingWeights.AsSpan(
                        offset,
                        TerrainCookedFormat.WeightChannelCount)
                    .CopyTo(nextWeights.AsSpan(
                        offset,
                        TerrainCookedFormat.WeightChannelCount));
            }
        }

        int[] previewChangedSamples = CollectChangedSamples(
            previousWorkingHeights,
            nextHeights,
            previousWorkingWeights,
            nextWeights);
        TerrainTileCoordinate[] previewChangedTiles = ResolveAffectedTiles(
            previewChangedSamples);
        if (previewChangedTiles.Length == 0 && hadLocalConflict)
        {
            int[] dirtySamples = previousDirtyHeight
                .Concat(previousDirtyWeight)
                .Distinct()
                .Order()
                .ToArray();
            previewChangedTiles = ResolveAffectedTiles(dirtySamples);
        }
        ValidateTransactionTileCount(previewChangedTiles, "external preview replacement");

        try
        {
            Array.Copy(nextHeights, m_Heights, nextHeights.Length);
            Array.Copy(externalHeights, m_OriginalHeights, externalHeights.Length);
            Array.Copy(nextWeights, m_Weights, nextWeights.Length);
            Array.Copy(externalWeights, m_OriginalWeights, externalWeights.Length);
            RebuildDirtySets(previousDirtyHeight, previousDirtyWeight);

            TerrainAuthoringPreviewRevision? revision = previewChangedTiles.Length == 0
                ? null
                : BuildRevision(previewChangedTiles);
            if (revision != null)
            {
                m_Revision = revision.Revision;
            }

            baseline.HeightBytes = diskHeightBytes;
            baseline.WeightBytes = diskWeightBytes;
            m_ExternalChanges = TerrainAuthoringExternalChanges.None;
            return new TerrainAuthoringSourceReimportResult(
                true,
                hadLocalConflict,
                resolution,
                revision,
                Array.AsReadOnly(externallyChangedTiles));
        }
        catch
        {
            Array.Copy(previousWorkingHeights, m_Heights, m_Heights.Length);
            Array.Copy(previousWorkingWeights, m_Weights, m_Weights.Length);
            Array.Copy(previousOriginalHeights, m_OriginalHeights, m_OriginalHeights.Length);
            Array.Copy(previousOriginalWeights, m_OriginalWeights, m_OriginalWeights.Length);
            m_DirtyHeightSamples.Clear();
            m_DirtyHeightSamples.UnionWith(previousDirtyHeight);
            m_DirtyWeightSamples.Clear();
            m_DirtyWeightSamples.UnionWith(previousDirtyWeight);
            RestorePreviewState(previousRoot, previousReferences);
            m_Revision = previousRevision;
            throw;
        }
    }

    private void InitializeSourceBaseline(AssetRecord rootAsset)
    {
        string assetsRoot = FindAssetsRoot(rootAsset.SourcePath);
        m_SourceBaseline = new SourceBaseline(
            rootAsset,
            assetsRoot,
            File.ReadAllBytes(rootAsset.SourcePath),
            File.ReadAllBytes(m_SourceRoot.HeightSource.ResolvedPath),
            m_SourceRoot.WeightSource == null
                ? null
                : File.ReadAllBytes(m_SourceRoot.WeightSource.ResolvedPath));
        m_ExternalChanges = TerrainAuthoringExternalChanges.None;
    }

    private TerrainTileCoordinate[] ResolveDirtyTiles()
    {
        int[] samples = m_DirtyHeightSamples
            .Concat(m_DirtyWeightSamples)
            .Distinct()
            .Order()
            .ToArray();
        return ResolveAffectedTiles(samples);
    }

    private void RebuildDirtySets(
        IReadOnlyList<int> candidateHeightSamples,
        IReadOnlyList<int> candidateWeightSamples)
    {
        m_DirtyHeightSamples.Clear();
        for (int index = 0; index < candidateHeightSamples.Count; index++)
        {
            UpdateHeightDirty(candidateHeightSamples[index]);
        }

        m_DirtyWeightSamples.Clear();
        for (int index = 0; index < candidateWeightSamples.Count; index++)
        {
            UpdateWeightDirty(candidateWeightSamples[index]);
        }
    }

    private static int[] CollectChangedSamples(
        ReadOnlySpan<ushort> oldHeights,
        ReadOnlySpan<ushort> newHeights,
        ReadOnlySpan<byte> oldWeights,
        ReadOnlySpan<byte> newWeights)
    {
        if (oldHeights.Length != newHeights.Length ||
            oldWeights.Length != newWeights.Length ||
            oldWeights.Length != oldHeights.Length * TerrainCookedFormat.WeightChannelCount)
        {
            throw new InvalidOperationException(
                "Terrain source comparison dimensions do not match.");
        }

        var changed = new List<int>();
        for (int sample = 0; sample < oldHeights.Length; sample++)
        {
            int weightOffset = sample * TerrainCookedFormat.WeightChannelCount;
            if (oldHeights[sample] != newHeights[sample] ||
                !oldWeights.Slice(weightOffset, TerrainCookedFormat.WeightChannelCount)
                    .SequenceEqual(newWeights.Slice(
                        weightOffset,
                        TerrainCookedFormat.WeightChannelCount)))
            {
                changed.Add(sample);
            }
        }
        return changed.ToArray();
    }

    private static void ValidateTransactionTileCount(
        IReadOnlyCollection<TerrainTileCoordinate> tiles,
        string operation)
    {
        if (tiles.Count > TerrainAuthoringLimits.MaximumAffectedTilesPerRevision)
        {
            throw new InvalidOperationException(
                $"Terrain {operation} affects {tiles.Count} tiles, exceeding the bounded " +
                $"limit {TerrainAuthoringLimits.MaximumAffectedTilesPerRevision}.");
        }
    }

    private SourceBaseline RequireSourceBaseline()
    {
        return m_SourceBaseline ?? throw new InvalidOperationException(
            "This terrain authoring document is not backed by editable source files.");
    }

    private void RestorePreviewState(
        CookedTerrainRoot root,
        IReadOnlyDictionary<TerrainTileCoordinate, CookedTerrainTileReference> references)
    {
        m_CurrentRoot = root;
        m_TileReferences.Clear();
        foreach ((TerrainTileCoordinate coordinate, CookedTerrainTileReference reference) in references)
        {
            m_TileReferences.Add(coordinate, reference);
        }
    }

    private static bool FileMatches(string path, ReadOnlySpan<byte> baseline)
    {
        try
        {
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline);
        }
        catch
        {
            return false;
        }
    }

    private static string FindAssetsRoot(string sourcePath)
    {
        DirectoryInfo? directory = new FileInfo(Path.GetFullPath(sourcePath)).Directory;
        while (directory != null)
        {
            if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Terrain source '{sourcePath}' is outside a package/workspace Assets root.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed class SourceBaseline
    {
        public SourceBaseline(
            AssetRecord rootAsset,
            string assetsRoot,
            byte[] rootBytes,
            byte[] heightBytes,
            byte[]? weightBytes)
        {
            RootAsset = rootAsset;
            AssetsRoot = assetsRoot;
            RootBytes = rootBytes;
            HeightBytes = heightBytes;
            WeightBytes = weightBytes;
        }

        public AssetRecord RootAsset { get; }
        public string AssetsRoot { get; }
        public byte[] RootBytes { get; }
        public byte[] HeightBytes { get; set; }
        public byte[]? WeightBytes { get; set; }
    }
}
