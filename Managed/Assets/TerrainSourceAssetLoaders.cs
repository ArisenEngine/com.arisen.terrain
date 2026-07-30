using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Terrain.Assets;

public static class TerrainRootSourceAssetLoader
{
    public const int MinimumSourceSchemaVersion = 1;
    public const int CurrentSourceSchemaVersion = 2;
    public const int MinTileResolution = 3;
    public const int MaxTileResolution = 4_097;

    public static TerrainRootSourceDescriptor LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainRootSourceAsset> terrainRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!terrainRef.IsValid)
        {
            throw new ArgumentException("Terrain root asset ref is empty.", nameof(terrainRef));
        }

        if (!assetDatabase.TryGetAsset(terrainRef, out AssetRecord? sourceAsset) ||
            !string.Equals(
                sourceAsset.AssetType,
                TerrainAssetTypes.Root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainRootSourceAssetLoader] Terrain root '{terrainRef.Guid:D}' is not indexed as '{TerrainAssetTypes.Root}'.");
        }

        if (!string.IsNullOrWhiteSpace(terrainRef.PackageId) &&
            !string.Equals(
                terrainRef.PackageId,
                sourceAsset.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainRootSourceAssetLoader] Terrain root '{terrainRef.Guid:D}' belongs to package " +
                $"'{sourceAsset.PackageId}', expected '{terrainRef.PackageId}'.");
        }

        return LoadSource(sourceAsset);
    }

    public static TerrainRootSourceDescriptor LoadSource(AssetRecord sourceAsset)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        if (!string.Equals(
                sourceAsset.AssetType,
                TerrainAssetTypes.Root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainRootSourceAssetLoader] Asset '{sourceAsset.Guid:D}' has type " +
                $"'{sourceAsset.AssetType}', expected '{TerrainAssetTypes.Root}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                $"[TerrainRootSourceAssetLoader] Terrain root source is missing: {sourceAsset.SourcePath}",
                sourceAsset.SourcePath);
        }

        return LoadSourceText(
            sourceAsset.Guid,
            sourceAsset.PackageId,
            sourceAsset.SourcePath,
            File.ReadAllText(sourceAsset.SourcePath));
    }

    public static TerrainRootSourceDescriptor LoadSourceText(
        Guid expectedTerrainGuid,
        string sourcePackageId,
        string sourcePath,
        string sourceText)
    {
        if (expectedTerrainGuid == Guid.Empty)
        {
            throw new ArgumentException("Terrain root source requires a stable asset GUID.", nameof(expectedTerrainGuid));
        }

        string packageId = TerrainSourceValidation.NormalizePackageId(
            sourcePackageId,
            "terrain root package id");
        TerrainRootSourceDocument source = TerrainSourceYaml.DeserializeRoot(sourcePath, sourceText);

        if (source.Version is < MinimumSourceSchemaVersion or > CurrentSourceSchemaVersion)
        {
            throw Invalid(
                sourcePath,
                $"schema version '{source.Version}' is unsupported; expected " +
                $"{MinimumSourceSchemaVersion}..{CurrentSourceSchemaVersion}");
        }

        if (source.TerrainGuid == Guid.Empty || source.TerrainGuid != expectedTerrainGuid)
        {
            throw Invalid(
                sourcePath,
                $"declares GUID '{source.TerrainGuid:D}', expected asset GUID '{expectedTerrainGuid:D}'");
        }

        string name = TerrainSourceValidation.NormalizeName(source.Name, sourcePath, "terrain root");
        WorldPosition placement = source.WorldPlacement?.ToPosition()
            ?? throw Invalid(sourcePath, "requires WorldPlacement");
        if (!placement.IsFinite)
        {
            throw Invalid(sourcePath, "WorldPlacement must contain finite values");
        }

        TerrainSampleSpacing sampleSpacing = source.SampleSpacing?.ToSpacing()
            ?? throw Invalid(sourcePath, "requires SampleSpacing");
        if (!sampleSpacing.IsValid)
        {
            throw Invalid(sourcePath, "SampleSpacing X/Z must be finite and greater than zero");
        }

        TerrainHeightRange heightRange = source.HeightRange?.ToRange()
            ?? throw Invalid(sourcePath, "requires HeightRange");
        if (!heightRange.IsValid)
        {
            throw Invalid(sourcePath, "HeightRange must be finite and satisfy Min < Max");
        }

        if (source.HeightSource == null || string.IsNullOrWhiteSpace(source.HeightSource.Path))
        {
            throw Invalid(sourcePath, "requires HeightSource.Path");
        }

        if (!string.Equals(
                source.HeightSource.Format,
                nameof(TerrainHeightSourceFormat.Pgm16BigEndianScalar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                sourcePath,
                $"HeightSource.Format '{source.HeightSource.Format}' is unsupported; expected " +
                nameof(TerrainHeightSourceFormat.Pgm16BigEndianScalar));
        }

        string authoredHeightPath = source.HeightSource.Path.Trim().Replace('\\', '/');
        string resolvedHeightPath = ResolveSourcePath(sourcePath, authoredHeightPath);
        if (!string.Equals(Path.GetExtension(resolvedHeightPath), ".pgm", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                sourcePath,
                $"height source '{authoredHeightPath}' must use the explicit '.pgm' extension");
        }

        if (!File.Exists(resolvedHeightPath))
        {
            throw new FileNotFoundException(
                $"[TerrainRootSourceAssetLoader] Terrain root '{sourcePath}' references missing height source '{resolvedHeightPath}'.",
                resolvedHeightPath);
        }

        TerrainHeightField heightField = TerrainHeightSourceDecoder.DecodeFile(resolvedHeightPath);
        TerrainWeightSourceDescriptor? weightSource = null;
        if (source.WeightSource != null)
        {
            if (source.Version < 2)
            {
                throw Invalid(sourcePath, "WeightSource requires schema version 2");
            }
            if (string.IsNullOrWhiteSpace(source.WeightSource.Path) ||
                !string.Equals(
                    source.WeightSource.Format,
                    nameof(TerrainWeightSourceFormat.Rgba8Hex),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    sourcePath,
                    $"WeightSource requires a path and Format '{nameof(TerrainWeightSourceFormat.Rgba8Hex)}'");
            }

            string authoredWeightPath = source.WeightSource.Path.Trim().Replace('\\', '/');
            string resolvedWeightPath = ResolveSourcePath(sourcePath, authoredWeightPath);
            if (!string.Equals(
                    Path.GetExtension(resolvedWeightPath),
                    ".ariweights",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    sourcePath,
                    $"weight source '{authoredWeightPath}' must use the explicit '.ariweights' extension");
            }

            TerrainWeightField weightField = TerrainWeightSourceDecoder.DecodeFile(resolvedWeightPath);
            if (weightField.Width != heightField.Width || weightField.Height != heightField.Height)
            {
                throw Invalid(
                    sourcePath,
                    $"weight dimensions {weightField.Width}x{weightField.Height} must match height " +
                    $"dimensions {heightField.Width}x{heightField.Height}");
            }

            weightSource = new TerrainWeightSourceDescriptor(
                authoredWeightPath,
                resolvedWeightPath,
                TerrainWeightSourceFormat.Rgba8Hex,
                weightField.Width,
                weightField.Height);
        }
        int tileResolution = source.TileResolution;
        int tileIntervals = tileResolution - 1;
        if (tileResolution < MinTileResolution ||
            tileResolution > MaxTileResolution ||
            (tileIntervals & (tileIntervals - 1)) != 0)
        {
            throw Invalid(
                sourcePath,
                $"TileResolution '{tileResolution}' must be 2^n + 1 within {MinTileResolution}..{MaxTileResolution}");
        }

        if (heightField.Width < tileResolution ||
            heightField.Height < tileResolution ||
            (heightField.Width - 1) % tileIntervals != 0 ||
            (heightField.Height - 1) % tileIntervals != 0)
        {
            throw Invalid(
                sourcePath,
                $"height dimensions {heightField.Width}x{heightField.Height} must be exact shared-edge multiples " +
                $"of TileResolution {tileResolution}");
        }

        if (!string.Equals(
                source.BorderPolicy,
                nameof(TerrainBorderPolicy.SharedEdgeSamples),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                sourcePath,
                $"BorderPolicy '{source.BorderPolicy}' is unsupported; expected " +
                nameof(TerrainBorderPolicy.SharedEdgeSamples));
        }

        TerrainTileCoordinate tileOrigin = source.TileOrigin?.ToCoordinate()
            ?? throw Invalid(sourcePath, "requires TileOrigin");
        TerrainTileIdentity.ValidateCoordinate(tileOrigin);

        if (source.LayerSet == null || source.LayerSet.Guid == Guid.Empty)
        {
            throw Invalid(sourcePath, "requires a non-empty LayerSet.Guid");
        }

        string layerPackageId = TerrainSourceValidation.NormalizePackageId(
            source.LayerSet.PackageId,
            "terrain layer-set package id");
        var layerSet = new AssetRef<TerrainLayerSetSourceAsset>(
            source.LayerSet.Guid,
            TerrainAssetTypes.LayerSet,
            layerPackageId);

        int tileCountX = (heightField.Width - 1) / tileIntervals;
        int tileCountZ = (heightField.Height - 1) / tileIntervals;
        TerrainGeneratedTileRecord[] expectedTiles = TerrainTileIdentity.CreateRecords(
            expectedTerrainGuid,
            packageId,
            tileOrigin,
            tileCountX,
            tileCountZ);
        ValidatePersistedTileRecords(
            source.GeneratedTiles,
            expectedTiles,
            expectedTerrainGuid,
            packageId,
            sourcePath);

        return new TerrainRootSourceDescriptor(
            expectedTerrainGuid,
            packageId,
            source.Version,
            name,
            placement,
            sampleSpacing,
            heightRange,
            new TerrainHeightSourceDescriptor(
                authoredHeightPath,
                resolvedHeightPath,
                TerrainHeightSourceFormat.Pgm16BigEndianScalar,
                heightField.Width,
                heightField.Height),
            tileResolution,
            TerrainBorderPolicy.SharedEdgeSamples,
            tileOrigin,
            layerSet,
            expectedTiles)
        {
            WeightSource = weightSource
        };
    }

    private static void ValidatePersistedTileRecords(
        IReadOnlyList<TerrainGeneratedTileSource>? persistedTiles,
        IReadOnlyList<TerrainGeneratedTileRecord> expectedTiles,
        Guid terrainGuid,
        string packageId,
        string sourcePath)
    {
        if (persistedTiles == null || persistedTiles.Count != expectedTiles.Count)
        {
            throw Invalid(
                sourcePath,
                $"GeneratedTiles count '{persistedTiles?.Count ?? 0}' does not match expected tile count '{expectedTiles.Count}'");
        }

        var expectedByCoordinate = expectedTiles.ToDictionary(tile => tile.Coordinate);
        var observed = new HashSet<TerrainTileCoordinate>();
        for (int index = 0; index < persistedTiles.Count; index++)
        {
            TerrainGeneratedTileSource sourceTile = persistedTiles[index];
            TerrainTileCoordinate coordinate = sourceTile.Coordinate?.ToCoordinate()
                ?? throw Invalid(sourcePath, $"GeneratedTiles[{index}] requires Coordinate");
            TerrainTileIdentity.ValidateCoordinate(coordinate);
            if (!observed.Add(coordinate))
            {
                throw Invalid(sourcePath, $"GeneratedTiles contains duplicate coordinate {coordinate}");
            }

            if (!expectedByCoordinate.ContainsKey(coordinate))
            {
                throw Invalid(
                    sourcePath,
                    $"GeneratedTiles coordinate {coordinate} is outside the height source tile grid");
            }

            Guid expectedGuid = TerrainTileIdentity.CreateGuid(terrainGuid, packageId, coordinate);
            if (sourceTile.Guid == Guid.Empty || sourceTile.Guid != expectedGuid)
            {
                throw Invalid(
                    sourcePath,
                    $"GeneratedTiles coordinate {coordinate} declares GUID '{sourceTile.Guid:D}', expected '{expectedGuid:D}'");
            }
        }
    }

    private static string ResolveSourcePath(string terrainSourcePath, string heightSourcePath)
    {
        if (Path.IsPathRooted(heightSourcePath))
        {
            return Path.GetFullPath(heightSourcePath);
        }

        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(terrainSourcePath))
            ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(sourceDirectory, heightSourcePath));
    }

    private static InvalidDataException Invalid(string path, string diagnostic)
    {
        return new InvalidDataException(
            $"[TerrainRootSourceAssetLoader] Terrain root '{path}' {diagnostic}.");
    }
}

public static class TerrainLayerSetSourceAssetLoader
{
    public const int MinimumSourceSchemaVersion = 1;
    public const int CurrentSourceSchemaVersion = 2;
    public const int MaxLayerCount = 4;

    public static TerrainLayerSetSourceDescriptor LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<TerrainLayerSetSourceAsset> layerSetRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!layerSetRef.IsValid)
        {
            throw new ArgumentException("Terrain layer-set asset ref is empty.", nameof(layerSetRef));
        }

        if (!assetDatabase.TryGetAsset(layerSetRef, out AssetRecord? sourceAsset) ||
            !string.Equals(
                sourceAsset.AssetType,
                TerrainAssetTypes.LayerSet,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainLayerSetSourceAssetLoader] Terrain layer set '{layerSetRef.Guid:D}' is not indexed as '{TerrainAssetTypes.LayerSet}'.");
        }

        if (!string.IsNullOrWhiteSpace(layerSetRef.PackageId) &&
            !string.Equals(
                layerSetRef.PackageId,
                sourceAsset.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainLayerSetSourceAssetLoader] Terrain layer set '{layerSetRef.Guid:D}' belongs to package " +
                $"'{sourceAsset.PackageId}', expected '{layerSetRef.PackageId}'.");
        }

        return LoadSource(sourceAsset);
    }

    public static TerrainLayerSetSourceDescriptor LoadSource(AssetRecord sourceAsset)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        if (!string.Equals(
                sourceAsset.AssetType,
                TerrainAssetTypes.LayerSet,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[TerrainLayerSetSourceAssetLoader] Asset '{sourceAsset.Guid:D}' has type " +
                $"'{sourceAsset.AssetType}', expected '{TerrainAssetTypes.LayerSet}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                $"[TerrainLayerSetSourceAssetLoader] Terrain layer-set source is missing: {sourceAsset.SourcePath}",
                sourceAsset.SourcePath);
        }

        return LoadSourceText(
            sourceAsset.Guid,
            sourceAsset.PackageId,
            sourceAsset.SourcePath,
            File.ReadAllText(sourceAsset.SourcePath));
    }

    public static TerrainLayerSetSourceDescriptor LoadSourceText(
        Guid expectedLayerSetGuid,
        string sourcePackageId,
        string sourcePath,
        string sourceText)
    {
        if (expectedLayerSetGuid == Guid.Empty)
        {
            throw new ArgumentException("Terrain layer set requires a stable asset GUID.", nameof(expectedLayerSetGuid));
        }

        string packageId = TerrainSourceValidation.NormalizePackageId(
            sourcePackageId,
            "terrain layer-set package id");
        TerrainLayerSetSourceDocument source = TerrainSourceYaml.DeserializeLayerSet(sourcePath, sourceText);

        if (source.Version is < MinimumSourceSchemaVersion or > CurrentSourceSchemaVersion)
        {
            throw Invalid(
                sourcePath,
                $"schema version '{source.Version}' is unsupported; expected " +
                $"{MinimumSourceSchemaVersion}..{CurrentSourceSchemaVersion}");
        }

        if (source.LayerSetGuid == Guid.Empty || source.LayerSetGuid != expectedLayerSetGuid)
        {
            throw Invalid(
                sourcePath,
                $"declares GUID '{source.LayerSetGuid:D}', expected asset GUID '{expectedLayerSetGuid:D}'");
        }

        string name = TerrainSourceValidation.NormalizeName(source.Name, sourcePath, "terrain layer set");
        if (source.Layers == null || source.Layers.Count == 0 || source.Layers.Count > MaxLayerCount)
        {
            throw Invalid(
                sourcePath,
                $"must declare between 1 and {MaxLayerCount} ordered layers");
        }

        var layers = new TerrainLayerDescriptor[source.Layers.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < source.Layers.Count; index++)
        {
            TerrainLayerSource layer = source.Layers[index];
            string id = TerrainSourceValidation.NormalizeLayerId(layer.Id, sourcePath, index);
            if (!ids.Add(id))
            {
                throw Invalid(sourcePath, $"contains duplicate layer id '{id}'");
            }

            TerrainLayerTint tint = layer.Tint?.ToTint() ?? TerrainLayerTint.White;
            TerrainLayerWorldTiling worldTiling =
                layer.WorldTiling?.ToWorldTiling() ?? TerrainLayerWorldTiling.Default;
            if (!tint.IsValid ||
                !worldTiling.IsValid ||
                !TerrainLayerMaterialLimits.IsValid(
                    layer.RoughnessMultiplier,
                    layer.MetallicMultiplier,
                    layer.NormalStrength))
            {
                throw Invalid(
                    sourcePath,
                    $"Layers[{index}] contains non-finite or out-of-range material parameters");
            }

            layers[index] = new TerrainLayerDescriptor(
                id,
                CreateTextureRef(layer.Albedo, sourcePath, index, "Albedo"),
                CreateTextureRef(layer.Normal, sourcePath, index, "Normal"),
                CreateTextureRef(layer.Orm, sourcePath, index, "Orm"),
                tint,
                layer.RoughnessMultiplier,
                layer.MetallicMultiplier,
                layer.NormalStrength,
                worldTiling);
        }

        return new TerrainLayerSetSourceDescriptor(
            expectedLayerSetGuid,
            packageId,
            source.Version,
            name,
            layers);
    }

    private static AssetRef<Texture2DSourceAsset> CreateTextureRef(
        TerrainAssetReferenceSource? source,
        string sourcePath,
        int layerIndex,
        string binding)
    {
        if (source == null || source.Guid == Guid.Empty)
        {
            throw Invalid(
                sourcePath,
                $"Layers[{layerIndex}].{binding}.Guid must be non-empty");
        }

        string packageId = TerrainSourceValidation.NormalizePackageId(
            source.PackageId,
            $"terrain layer {binding} package id");
        return new AssetRef<Texture2DSourceAsset>(
            source.Guid,
            "Texture2D",
            packageId);
    }

    private static InvalidDataException Invalid(string path, string diagnostic)
    {
        return new InvalidDataException(
            $"[TerrainLayerSetSourceAssetLoader] Terrain layer set '{path}' {diagnostic}.");
    }
}

internal static class TerrainSourceValidation
{
    public static string NormalizePackageId(string? packageId, string context)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new InvalidDataException($"{context} must be non-empty.");
        }

        string normalized = packageId.Trim().Replace('\\', '/').ToLowerInvariant();
        if (normalized.Length > 128 ||
            normalized.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException(
                $"{context} '{packageId}' must contain only ASCII letters, digits, '.', '-', or '_'.");
        }

        return normalized;
    }

    public static string NormalizeName(string? name, string path, string context)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 512)
        {
            throw new InvalidDataException(
                $"{context} '{path}' requires a name no longer than 512 characters.");
        }

        return name.Trim();
    }

    public static string NormalizeLayerId(string? id, string path, int index)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException(
                $"Terrain layer set '{path}' Layers[{index}].Id must be non-empty.");
        }

        string normalized = id.Trim().ToLowerInvariant();
        if (normalized.Length > 64 ||
            normalized.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException(
                $"Terrain layer id '{id}' must contain only ASCII letters, digits, '.', '-', or '_'.");
        }

        return normalized;
    }
}

internal static class TerrainSourceYaml
{
    public static TerrainRootSourceDocument DeserializeRoot(string path, string sourceText)
    {
        YamlMappingNode root = ParseRoot(path, sourceText, "terrain root");
        ValidateMapping(
            root,
            path,
            "terrain root",
            "Version",
            "TerrainGuid",
            "Name",
            "WorldPlacement",
            "SampleSpacing",
            "HeightRange",
            "HeightSource",
            "WeightSource",
            "TileResolution",
            "BorderPolicy",
            "TileOrigin",
            "LayerSet",
            "GeneratedTiles");
        ValidateChildMapping(root, "WorldPlacement", path, "world placement", "X", "Y", "Z");
        ValidateChildMapping(root, "SampleSpacing", path, "sample spacing", "X", "Z");
        ValidateChildMapping(root, "HeightRange", path, "height range", "Min", "Max");
        ValidateChildMapping(root, "HeightSource", path, "height source", "Path", "Format");
        ValidateOptionalChildMapping(root, "WeightSource", path, "weight source", "Path", "Format");
        ValidateChildMapping(root, "TileOrigin", path, "tile origin", "X", "Z");
        ValidateChildMapping(root, "LayerSet", path, "layer set reference", "Guid", "PackageId");
        ValidateSequence(root, "GeneratedTiles", path, "generated tiles", tile =>
        {
            ValidateMapping(tile, path, "generated tile", "Coordinate", "Guid");
            ValidateChildMapping(tile, "Coordinate", path, "generated tile coordinate", "X", "Z");
        });
        return Deserialize<TerrainRootSourceDocument>(path, sourceText, "terrain root");
    }

    public static TerrainLayerSetSourceDocument DeserializeLayerSet(string path, string sourceText)
    {
        YamlMappingNode root = ParseRoot(path, sourceText, "terrain layer set");
        ValidateMapping(root, path, "terrain layer set", "Version", "LayerSetGuid", "Name", "Layers");
        ValidateSequence(root, "Layers", path, "terrain layers", layer =>
        {
            ValidateMapping(
                layer,
                path,
                "terrain layer",
                "Id",
                "Albedo",
                "Normal",
                "Orm",
                "Tint",
                "RoughnessMultiplier",
                "MetallicMultiplier",
                "NormalStrength",
                "WorldTiling");
            ValidateChildMapping(layer, "Albedo", path, "albedo reference", "Guid", "PackageId");
            ValidateChildMapping(layer, "Normal", path, "normal reference", "Guid", "PackageId");
            ValidateChildMapping(layer, "Orm", path, "ORM reference", "Guid", "PackageId");
            ValidateOptionalChildMapping(layer, "Tint", path, "terrain layer tint", "R", "G", "B", "A");
            ValidateOptionalChildMapping(layer, "WorldTiling", path, "terrain layer world tiling", "X", "Z");
        });
        return Deserialize<TerrainLayerSetSourceDocument>(path, sourceText, "terrain layer set");
    }

    private static YamlMappingNode ParseRoot(string path, string sourceText, string context)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(sourceText);
            stream.Load(reader);
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidDataException(
                    $"{context} '{path}' must contain one YAML mapping document.");
            }

            return root;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to parse {context} '{path}': {ex.Message}",
                ex);
        }
    }

    private static T Deserialize<T>(string path, string sourceText, string context)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<T>(sourceText)
                ?? throw new InvalidDataException($"{context} '{path}' deserialized to an empty document.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to deserialize {context} '{path}': {ex.Message}",
                ex);
        }
    }

    private static void ValidateChildMapping(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        params string[] allowed)
    {
        if (!TryGetNode(root, key, out YamlNode node) || node is not YamlMappingNode child)
        {
            throw new InvalidDataException(
                $"Terrain source '{path}' requires {context} mapping '{key}'.");
        }

        ValidateMapping(child, path, context, allowed);
    }

    private static void ValidateOptionalChildMapping(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        params string[] allowed)
    {
        if (!TryGetNode(root, key, out YamlNode node))
        {
            return;
        }

        if (node is not YamlMappingNode child)
        {
            throw new InvalidDataException(
                $"Terrain source '{path}' requires {context} mapping '{key}'.");
        }

        ValidateMapping(child, path, context, allowed);
    }

    private static void ValidateSequence(
        YamlMappingNode root,
        string key,
        string path,
        string context,
        Action<YamlMappingNode> validate)
    {
        if (!TryGetNode(root, key, out YamlNode node) || node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException(
                $"Terrain source '{path}' requires {context} sequence '{key}'.");
        }

        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode mapping)
            {
                throw new InvalidDataException(
                    $"Terrain source '{path}' {context} entries must be mappings.");
            }

            validate(mapping);
        }
    }

    private static void ValidateMapping(
        YamlMappingNode mapping,
        string path,
        string context,
        params string[] allowed)
    {
        var allowedKeys = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Terrain source '{path}' {context} contains a non-scalar key.");
            }

            if (!observed.Add(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Terrain source '{path}' {context} contains duplicate key '{scalar.Value}'.");
            }

            if (!allowedKeys.Contains(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Terrain source '{path}' {context} contains unknown field '{scalar.Value}'.");
            }
        }
    }

    private static bool TryGetNode(YamlMappingNode root, string key, out YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode value) in root.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                node = value;
                return true;
            }
        }

        node = null!;
        return false;
    }
}

internal sealed class TerrainRootSourceDocument
{
    public int Version { get; set; }
    public Guid TerrainGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public TerrainWorldPlacementSource? WorldPlacement { get; set; }
    public TerrainSampleSpacingSource? SampleSpacing { get; set; }
    public TerrainHeightRangeSource? HeightRange { get; set; }
    public TerrainHeightSourceReference? HeightSource { get; set; }
    public TerrainWeightSourceReference? WeightSource { get; set; }
    public int TileResolution { get; set; }
    public string BorderPolicy { get; set; } = string.Empty;
    public TerrainTileCoordinateSource? TileOrigin { get; set; }
    public TerrainAssetReferenceSource? LayerSet { get; set; }
    public List<TerrainGeneratedTileSource> GeneratedTiles { get; set; } = new();
}

internal sealed class TerrainLayerSetSourceDocument
{
    public int Version { get; set; }
    public Guid LayerSetGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<TerrainLayerSource> Layers { get; set; } = new();
}

internal sealed class TerrainWorldPlacementSource
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public WorldPosition ToPosition() => new(X, Y, Z);
}

internal sealed class TerrainSampleSpacingSource
{
    public double X { get; set; }
    public double Z { get; set; }

    public TerrainSampleSpacing ToSpacing() => new(X, Z);
}

internal sealed class TerrainHeightRangeSource
{
    public double Min { get; set; }
    public double Max { get; set; }

    public TerrainHeightRange ToRange() => new(Min, Max);
}

internal sealed class TerrainHeightSourceReference
{
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

internal sealed class TerrainWeightSourceReference
{
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}

internal sealed class TerrainTileCoordinateSource
{
    public int X { get; set; }
    public int Z { get; set; }

    public TerrainTileCoordinate ToCoordinate() => new(X, Z);
}

internal sealed class TerrainAssetReferenceSource
{
    public Guid Guid { get; set; }
    public string PackageId { get; set; } = string.Empty;
}

internal sealed class TerrainGeneratedTileSource
{
    public TerrainTileCoordinateSource? Coordinate { get; set; }
    public Guid Guid { get; set; }
}

internal sealed class TerrainLayerSource
{
    public string Id { get; set; } = string.Empty;
    public TerrainAssetReferenceSource? Albedo { get; set; }
    public TerrainAssetReferenceSource? Normal { get; set; }
    public TerrainAssetReferenceSource? Orm { get; set; }
    public TerrainLayerTintSource? Tint { get; set; }
    public float RoughnessMultiplier { get; set; } = 1.0f;
    public float MetallicMultiplier { get; set; } = 1.0f;
    public float NormalStrength { get; set; } = 1.0f;
    public TerrainLayerWorldTilingSource? WorldTiling { get; set; }
}

internal sealed class TerrainLayerTintSource
{
    public float R { get; set; } = 1.0f;
    public float G { get; set; } = 1.0f;
    public float B { get; set; } = 1.0f;
    public float A { get; set; } = 1.0f;

    public TerrainLayerTint ToTint() => new(R, G, B, A);
}

internal sealed class TerrainLayerWorldTilingSource
{
    public float X { get; set; } = TerrainLayerWorldTiling.Default.X;
    public float Z { get; set; } = TerrainLayerWorldTiling.Default.Z;

    public TerrainLayerWorldTiling ToWorldTiling() => new(X, Z);
}
