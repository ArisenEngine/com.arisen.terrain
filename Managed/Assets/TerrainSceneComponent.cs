using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;
using YamlDotNet.RepresentationModel;

namespace ArisenEngine.Terrain;

[Flags]
public enum TerrainTileFlags : uint
{
    None = 0,
    Visible = 1 << 0,
    CastShadows = 1 << 1,
    ReceiveShadows = 1 << 2,
    PreferHighQuality = 1 << 3
}

[StructLayout(LayoutKind.Sequential)]
public struct TerrainTileComponent : IComponent
{
    public Guid TerrainRootGuid;
    public Guid TileGuid;
    public Guid LayerSetGuid;
    public int TileX;
    public int TileZ;
    public double WorldX;
    public double WorldY;
    public double WorldZ;
    public TerrainTileFlags Flags;

    public bool IsVisible => (Flags & TerrainTileFlags.Visible) != 0;

    public WorldPosition WorldPlacement => new(WorldX, WorldY, WorldZ);
}

public static class TerrainTileEntityIdentity
{
    private static readonly byte[] s_Domain =
        Encoding.ASCII.GetBytes("Arisen.TerrainTileEntity.v1");

    public static Guid Create(Guid sceneGuid, Guid tileGuid)
    {
        if (sceneGuid == Guid.Empty || tileGuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Terrain tile entity identity requires non-empty scene and tile GUIDs.");
        }

        Span<byte> identity = stackalloc byte[s_Domain.Length + 32];
        s_Domain.CopyTo(identity);
        sceneGuid.TryWriteBytes(identity[s_Domain.Length..], bigEndian: true, out _);
        tileGuid.TryWriteBytes(identity[(s_Domain.Length + 16)..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity, hash);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}

public static class TerrainTileBorderOwnership
{
    public static bool OwnsSample(
        int sampleX,
        int sampleZ,
        int resolution,
        in TerrainTileNeighborSet neighbors)
    {
        if (resolution < TerrainRootSourceAssetLoader.MinTileResolution ||
            (uint)sampleX >= (uint)resolution ||
            (uint)sampleZ >= (uint)resolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleX),
                "Terrain sample is outside the tile grid.");
        }

        // Interior positive borders belong to the positive neighbor. Outer root borders remain owned.
        return (sampleX != resolution - 1 || neighbors.PositiveX == Guid.Empty) &&
               (sampleZ != resolution - 1 || neighbors.PositiveZ == Guid.Empty);
    }
}

public sealed class TerrainTileSceneComponentCodec : ISceneComponentExtensionCodec
{
    public const uint TypeId = 0x54455252;
    public const int CurrentVersion = 1;

    private const int HeaderSize = 92;
    private const int MaxPackageIdBytes = 256;
    private const TerrainTileFlags SupportedFlags =
        TerrainTileFlags.Visible |
        TerrainTileFlags.CastShadows |
        TerrainTileFlags.ReceiveShadows |
        TerrainTileFlags.PreferHighQuality;

    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    public SceneComponentSchemaInfo Schema { get; } = new(
        TypeId,
        "TerrainTile",
        CurrentVersion,
        Required: true);

    public bool TryReadSource(
        in SceneComponentReadContext context,
        YamlMappingNode source,
        out object component,
        out string diagnostic)
    {
        try
        {
            ValidateFields(
                source,
                [
                    "TerrainRoot",
                    "TileGuid",
                    "LayerSet",
                    "Coordinate",
                    "WorldPlacement",
                    "Visible",
                    "CastShadows",
                    "ReceiveShadows",
                    "PreferHighQuality"
                ],
                "TerrainTile");
            YamlMappingNode root = ReadMapping(source, "TerrainRoot");
            YamlMappingNode layerSet = ReadMapping(source, "LayerSet");
            YamlMappingNode coordinate = ReadMapping(source, "Coordinate");
            YamlMappingNode worldPlacement = ReadMapping(source, "WorldPlacement");
            ValidateFields(root, ["Guid", "PackageId"], "TerrainTile.TerrainRoot");
            ValidateFields(layerSet, ["Guid", "PackageId"], "TerrainTile.LayerSet");
            ValidateFields(coordinate, ["X", "Z"], "TerrainTile.Coordinate");
            ValidateFields(worldPlacement, ["X", "Y", "Z"], "TerrainTile.WorldPlacement");

            var value = new TerrainTileSceneValue(
                NormalizePackageId(ReadString(root, "PackageId")),
                NormalizePackageId(ReadString(layerSet, "PackageId")),
                new TerrainTileComponent
                {
                    TerrainRootGuid = ReadGuid(root, "Guid"),
                    TileGuid = ReadGuid(source, "TileGuid"),
                    LayerSetGuid = ReadGuid(layerSet, "Guid"),
                    TileX = ReadInt32(coordinate, "X"),
                    TileZ = ReadInt32(coordinate, "Z"),
                    WorldX = ReadDouble(worldPlacement, "X"),
                    WorldY = ReadDouble(worldPlacement, "Y"),
                    WorldZ = ReadDouble(worldPlacement, "Z"),
                    Flags = BuildFlags(source)
                });
            if (!TryValidate(context, value, out diagnostic))
            {
                component = null!;
                return false;
            }

            component = value;
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            component = null!;
            diagnostic = Invalid(context, ex.Message);
            return false;
        }
    }

    public byte[] WriteCooked(object component)
    {
        TerrainTileSceneValue value = RequireValue(component);
        ValidateBasic(value);
        byte[] rootPackage = EncodePackageId(value.RootPackageId);
        byte[] layerPackage = EncodePackageId(value.LayerSetPackageId);
        byte[] output = new byte[checked(HeaderSize + rootPackage.Length + layerPackage.Length)];
        Span<byte> bytes = output;
        WriteGuid(bytes[..16], value.Component.TerrainRootGuid);
        WriteGuid(bytes[16..32], value.Component.TileGuid);
        WriteGuid(bytes[32..48], value.Component.LayerSetGuid);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], value.Component.TileX);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[52..], value.Component.TileZ);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[56..], value.Component.WorldX);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[64..], value.Component.WorldY);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[72..], value.Component.WorldZ);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[80..], (uint)value.Component.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[84..], checked((uint)rootPackage.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[88..], checked((uint)layerPackage.Length));
        rootPackage.CopyTo(bytes[HeaderSize..]);
        layerPackage.CopyTo(bytes[(HeaderSize + rootPackage.Length)..]);
        return output;
    }

    public bool TryReadCooked(
        in SceneComponentReadContext context,
        ReadOnlySpan<byte> payload,
        out object component,
        out string diagnostic)
    {
        try
        {
            if (payload.Length < HeaderSize)
            {
                throw new InvalidDataException("cooked payload header is truncated");
            }

            int rootPackageLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[84..]));
            int layerPackageLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[88..]));
            if (rootPackageLength <= 0 || rootPackageLength > MaxPackageIdBytes ||
                layerPackageLength <= 0 || layerPackageLength > MaxPackageIdBytes ||
                payload.Length != checked(HeaderSize + rootPackageLength + layerPackageLength))
            {
                throw new InvalidDataException("cooked package lengths or total payload size are invalid");
            }

            string rootPackage = NormalizePackageId(
                s_StrictUtf8.GetString(payload.Slice(HeaderSize, rootPackageLength)));
            string layerPackage = NormalizePackageId(
                s_StrictUtf8.GetString(payload.Slice(
                    HeaderSize + rootPackageLength,
                    layerPackageLength)));
            var value = new TerrainTileSceneValue(
                rootPackage,
                layerPackage,
                new TerrainTileComponent
                {
                    TerrainRootGuid = ReadGuid(payload[..16]),
                    TileGuid = ReadGuid(payload[16..32]),
                    LayerSetGuid = ReadGuid(payload[32..48]),
                    TileX = BinaryPrimitives.ReadInt32LittleEndian(payload[48..]),
                    TileZ = BinaryPrimitives.ReadInt32LittleEndian(payload[52..]),
                    WorldX = BinaryPrimitives.ReadDoubleLittleEndian(payload[56..]),
                    WorldY = BinaryPrimitives.ReadDoubleLittleEndian(payload[64..]),
                    WorldZ = BinaryPrimitives.ReadDoubleLittleEndian(payload[72..]),
                    Flags = (TerrainTileFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload[80..])
                });
            if (!TryValidate(context, value, out diagnostic))
            {
                component = null!;
                return false;
            }

            component = value;
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            component = null!;
            diagnostic = Invalid(context, ex.Message);
            return false;
        }
    }

    public IReadOnlyList<CookedSceneDependency> GetDependencies(object component)
    {
        TerrainTileSceneValue value = RequireValue(component);
        return
        [
            new CookedSceneDependency(
                value.Component.TerrainRootGuid,
                value.RootPackageId,
                TerrainAssetTypes.Root,
                Required: true,
                Variant: TerrainRootAssetCooker.RuntimeVariant),
            new CookedSceneDependency(
                value.Component.TileGuid,
                value.RootPackageId,
                TerrainAssetTypes.Tile,
                Required: true,
                Variant: TerrainTileAssetCooker.RuntimeVariant)
        ];
    }

    public Guid GetExclusiveOwnershipId(object component)
    {
        return RequireValue(component).Component.TileGuid;
    }

    public void AddToEntity(EntityManager entityManager, Entity entity, object component)
    {
        ArgumentNullException.ThrowIfNull(entityManager);
        entityManager.AddComponent(entity, RequireValue(component).Component);
    }

    private static bool TryValidate(
        in SceneComponentReadContext context,
        TerrainTileSceneValue value,
        out string diagnostic)
    {
        try
        {
            ValidateBasic(value);
            IAssetDatabase database = context.AssetDatabase;
            if (!database.TryGetAssetDescriptor(
                    value.Component.TerrainRootGuid,
                    out AssetDescriptor rootDescriptor) ||
                !string.Equals(
                    rootDescriptor.AssetType,
                    TerrainAssetTypes.Root,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    rootDescriptor.PackageId,
                    value.RootPackageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("terrain-root asset identity or package ownership is invalid");
            }

            if (!database.TryGetAssetDescriptor(
                    value.Component.TileGuid,
                    out AssetDescriptor tileDescriptor) ||
                !string.Equals(
                    tileDescriptor.AssetType,
                    TerrainAssetTypes.Tile,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    tileDescriptor.PackageId,
                    value.RootPackageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("terrain-tile asset identity or package ownership is invalid");
            }

            if (database.CanReadSourceAssets)
            {
                TerrainRootSourceDescriptor root = TerrainRootSourceAssetLoader.LoadSource(
                    database,
                    new AssetRef<TerrainRootSourceAsset>(
                        value.Component.TerrainRootGuid,
                        TerrainAssetTypes.Root,
                        value.RootPackageId));
                ValidateAgainstRoot(
                    value,
                    root.LayerSet.Guid,
                    root.LayerSet.PackageId,
                    root.WorldPlacement,
                    root.SampleSpacing,
                    root.TileResolution,
                    root.TileOrigin,
                    root.GeneratedTiles);

                if (!database.TryGetAssetDescriptor(
                        value.Component.LayerSetGuid,
                        out AssetDescriptor layerDescriptor) ||
                    !string.Equals(
                        layerDescriptor.AssetType,
                        TerrainAssetTypes.LayerSet,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        layerDescriptor.PackageId,
                        value.LayerSetPackageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "terrain layer-set asset identity or package ownership is invalid");
                }
            }
            else
            {
                if (!TerrainRootAssetCooker.TryLoadCooked(
                        database,
                        new AssetRef<TerrainRootSourceAsset>(
                            value.Component.TerrainRootGuid,
                            TerrainAssetTypes.Root,
                            value.RootPackageId),
                        out CookedTerrainRoot root,
                        out string rootDiagnostic))
                {
                    throw new InvalidDataException(rootDiagnostic);
                }

                ValidateAgainstRoot(
                    value,
                    root.LayerSetGuid,
                    root.LayerSetPackageId,
                    root.WorldPlacement,
                    root.SampleSpacing,
                    root.TileResolution,
                    root.TileOrigin,
                    root.Tiles.Select(tile =>
                        new TerrainGeneratedTileRecord(tile.Coordinate, tile.Guid)));
            }

            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            diagnostic = Invalid(context, ex.Message);
            return false;
        }
    }

    private static void ValidateAgainstRoot(
        TerrainTileSceneValue value,
        Guid expectedLayerSetGuid,
        string expectedLayerSetPackageId,
        WorldPosition rootPlacement,
        TerrainSampleSpacing spacing,
        int tileResolution,
        TerrainTileCoordinate tileOrigin,
        IEnumerable<TerrainGeneratedTileRecord> tiles)
    {
        var coordinate = new TerrainTileCoordinate(
            value.Component.TileX,
            value.Component.TileZ);
        TerrainGeneratedTileRecord tile = tiles.SingleOrDefault(candidate =>
            candidate.Coordinate == coordinate);
        if (tile.Guid == Guid.Empty || tile.Guid != value.Component.TileGuid)
        {
            throw new InvalidDataException(
                $"terrain tile '{value.Component.TileGuid:D}' does not belong to root coordinate {coordinate}");
        }

        if (expectedLayerSetGuid != value.Component.LayerSetGuid ||
            !string.Equals(
                NormalizePackageId(expectedLayerSetPackageId),
                value.LayerSetPackageId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("terrain layer-set identity does not match its root");
        }

        int intervals = tileResolution - 1;
        var expectedPlacement = new WorldPosition(
            rootPlacement.X + checked(coordinate.X - tileOrigin.X) * intervals * spacing.X,
            rootPlacement.Y,
            rootPlacement.Z + checked(coordinate.Z - tileOrigin.Z) * intervals * spacing.Z);
        if (!expectedPlacement.IsFinite || expectedPlacement != value.Component.WorldPlacement)
        {
            throw new InvalidDataException(
                $"terrain tile world placement {value.Component.WorldPlacement} does not match root placement {expectedPlacement}");
        }
    }

    private static void ValidateBasic(TerrainTileSceneValue value)
    {
        TerrainTileComponent component = value.Component;
        if (component.TerrainRootGuid == Guid.Empty ||
            component.TileGuid == Guid.Empty ||
            component.LayerSetGuid == Guid.Empty ||
            (component.Flags & ~SupportedFlags) != 0 ||
            !component.WorldPlacement.IsFinite)
        {
            throw new InvalidDataException(
                "terrain identities, flags, or world placement are invalid");
        }

        TerrainTileIdentity.ValidateCoordinate(
            new TerrainTileCoordinate(component.TileX, component.TileZ));
        NormalizePackageId(value.RootPackageId);
        NormalizePackageId(value.LayerSetPackageId);
    }

    private static TerrainTileFlags BuildFlags(YamlMappingNode source)
    {
        TerrainTileFlags flags = TerrainTileFlags.None;
        if (ReadBoolean(source, "Visible")) flags |= TerrainTileFlags.Visible;
        if (ReadBoolean(source, "CastShadows")) flags |= TerrainTileFlags.CastShadows;
        if (ReadBoolean(source, "ReceiveShadows")) flags |= TerrainTileFlags.ReceiveShadows;
        if (ReadBoolean(source, "PreferHighQuality")) flags |= TerrainTileFlags.PreferHighQuality;
        return flags;
    }

    private static TerrainTileSceneValue RequireValue(object component)
    {
        return component as TerrainTileSceneValue
            ?? throw new ArgumentException(
                $"Terrain scene codec expected '{nameof(TerrainTileSceneValue)}' staging data.",
                nameof(component));
    }

    private static byte[] EncodePackageId(string packageId)
    {
        byte[] bytes = s_StrictUtf8.GetBytes(NormalizePackageId(packageId));
        if (bytes.Length == 0 || bytes.Length > MaxPackageIdBytes)
        {
            throw new InvalidDataException(
                $"terrain package id UTF-8 length must be within 1..{MaxPackageIdBytes}");
        }
        return bytes;
    }

    private static string NormalizePackageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException("terrain package id must be non-empty canonical text");
        }

        return value.ToLowerInvariant();
    }

    private static void ValidateFields(
        YamlMappingNode mapping,
        IReadOnlyCollection<string> supported,
        string context)
    {
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode key ||
                string.IsNullOrWhiteSpace(key.Value) ||
                !supported.Contains(key.Value))
            {
                throw new InvalidDataException(
                    $"{context} contains unknown field '{(keyNode as YamlScalarNode)?.Value ?? "<non-scalar>"}'");
            }
        }
    }

    private static YamlMappingNode ReadMapping(YamlMappingNode mapping, string key)
    {
        return ReadNode(mapping, key) as YamlMappingNode
            ?? throw new InvalidDataException($"{key} must be a mapping");
    }

    private static string ReadString(YamlMappingNode mapping, string key)
    {
        return (ReadNode(mapping, key) as YamlScalarNode)?.Value
            ?? throw new InvalidDataException($"{key} must be a scalar");
    }

    private static Guid ReadGuid(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!Guid.TryParse(text, out Guid value) || value == Guid.Empty)
        {
            throw new InvalidDataException($"{key} must be a non-empty GUID");
        }
        return value;
    }

    private static int ReadInt32(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException($"{key} must be a 32-bit integer");
        }
        return value;
    }

    private static double ReadDouble(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            throw new InvalidDataException($"{key} must be a finite number");
        }
        return value;
    }

    private static bool ReadBoolean(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!bool.TryParse(text, out bool value))
        {
            throw new InvalidDataException($"{key} must be true or false");
        }
        return value;
    }

    private static YamlNode ReadNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode keyNode, YamlNode value) in mapping.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new InvalidDataException($"required field '{key}' is missing");
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("Failed to write terrain component GUID.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source)
    {
        return new Guid(source, bigEndian: true);
    }

    private static string Invalid(in SceneComponentReadContext context, string message)
    {
        return $"[TerrainSceneComponent] Scene '{context.DiagnosticPath}' entity " +
               $"'{context.EntityGuid:D}' is invalid: {message}.";
    }

    private sealed record TerrainTileSceneValue(
        string RootPackageId,
        string LayerSetPackageId,
        TerrainTileComponent Component);
}
