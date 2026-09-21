using System.Numerics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain;

/// <summary>
/// Camera poses the terrain-streaming smoke fixture derives from the loaded terrain bounds.
/// </summary>
internal readonly record struct TerrainStreamingCameraPoses(
    WorldPosition NearPosition,
    Quaternion NearRotation,
    WorldPosition BoundaryPosition,
    Quaternion BoundaryRotation,
    WorldPosition FarPosition,
    Quaternion FarRotation);

/// <summary>
/// World-space terrain surface height at a horizontal position, or <see cref="double.NaN"/> when
/// the runtime cannot answer for that position.
/// </summary>
internal delegate double TerrainSurfaceHeightSampler(double worldX, double worldZ);

/// <summary>
/// Camera path owned by the terrain-streaming smoke fixture.
///
/// Every checkpoint the fixture captures has to satisfy three independent contracts at once. The
/// checkpoint capture rejects a tile that reports no selected patch, so the frustum has to reach
/// every canonical tile of the root; the visual capture rejects a frame that writes no depth, so the
/// frame has to contain the terrain surface rather than only sky; and the summary validator rejects
/// an inverted frame whose ground sits above the horizon in the image, so the surface has to stay
/// under a view that keeps the sky on top.
///
/// The coverage contract is a constraint on where a captured pose can stand once the root grows
/// past a single view. The frustum spans roughly 73 degrees horizontally at the fixture's 45 degree
/// vertical field of view and 16:9 aspect, while the canonical ShowcaseValley root covers a 512 m
/// square: a pose anywhere inside that square has the raster all around it, so the tiles behind and
/// beside the view direction fall outside the frustum and select no patch at all. Only the root's
/// corner regions put the whole raster ahead of the camera, because the square occupies a single
/// quadrant seen from a corner and the frustum wedge still reaches the opposite corner's tiles.
/// Every captured pose therefore stands <see cref="CornerInsetFraction"/> inside one of the four
/// corners of the root.
///
/// The authored showcase camera remains the scene's own starting view, and the fixture cannot
/// inherit it: it stands inside the raster, where no aim can frame the whole root. The fixture keeps
/// the authored *bearing* instead: the near pose takes the corner nearest the authored position, so
/// the valley is still viewed along the axis the scene author framed. Every derived pose stands
/// <see cref="EyeClearanceMetres"/> above the terrain surface at its own horizontal position -
/// inheriting the authored height would walk a pose that moves across the raster into the hillside,
/// capture the inside of the terrain through back-face culling, and read back a frame whose lower
/// part holds no written depth - and every derived pose aims at the bounds centre with the elevation
/// clamped into a shallow downward band, which keeps the sky over the ridge line and the streaming
/// surface under the view in the same frame.
/// </summary>
internal static class TerrainStreamingCameraPath
{
    /// <summary>
    /// Fraction of the terrain extent a captured pose stands inside the root bounds, measured from
    /// the corner it is anchored to along both axes. The pose has to stand inside the raster so the
    /// runtime surface query answers for its own horizontal position and the eye height stays a few
    /// metres above the ground. The inset stays small because a pose that walks away from its corner
    /// widens the angle the raster subtends, and the frustum wedge has to keep covering it.
    /// </summary>
    public const float CornerInsetFraction = 0.02f;

    /// <summary>
    /// Height of every derived fixture camera above the terrain surface at its own horizontal
    /// position. Inheriting the authored camera height is not an option: that height is only above
    /// the surface at the authored position, and the fixture moves the camera across ridges that
    /// are tens of metres higher than the valley floor the authored pose stands on.
    /// </summary>
    public const float EyeClearanceMetres = 6.0f;

    /// <summary>Lowest aim elevation the fixture allows, in degrees.</summary>
    public const float MinimumElevationDegrees = -12.0f;

    /// <summary>
    /// Highest aim elevation the fixture allows, in degrees. The band stays under the horizon on
    /// purpose: a frame that looks up at the ridge line loses the surface, a frame that looks past
    /// it keeps sky above the terrain, so the capture reads back as an upright landscape instead of
    /// an ambiguous inside-the-hill view. The bounds centre sits far above a valley pose and far
    /// below a ridge pose, so the exact aim at it is clamped in both directions.
    /// </summary>
    public const float MaximumElevationDegrees = -4.0f;

    private const double DegenerateHorizontalEpsilon = 1.0e-3;

    public static TerrainStreamingCameraPoses Build(
        in TerrainPatchWorldBounds rootBounds,
        WorldPosition authoredPosition,
        TerrainSurfaceHeightSampler? surfaceHeight = null)
    {
        if (!rootBounds.IsValid)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera path requires valid terrain root bounds.");
        }

        if (!authoredPosition.IsFinite)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera path requires a finite authored camera position.");
        }

        WorldPosition[] corners = CornerPoses(rootBounds, authoredPosition, surfaceHeight);
        return new TerrainStreamingCameraPoses(
            corners[0],
            Aim(rootBounds, corners[0]),
            corners[1],
            Aim(rootBounds, corners[1]),
            corners[^1],
            Aim(rootBounds, corners[^1]));
    }

    /// <summary>
    /// The four corner regions of the terrain root, ordered by their distance to the authored camera
    /// position so every fixture pose stays deterministic: the closest corner is the near pose, the
    /// next one the boundary pose, and the farthest the far pose. Every one of them frames the whole
    /// root, and the three captures that use them are three different views of it.
    /// </summary>
    private static WorldPosition[] CornerPoses(
        in TerrainPatchWorldBounds rootBounds,
        WorldPosition authoredPosition,
        TerrainSurfaceHeightSampler? surfaceHeight)
    {
        double insetX = (rootBounds.Max.X - rootBounds.Min.X) * CornerInsetFraction;
        double insetZ = (rootBounds.Max.Z - rootBounds.Min.Z) * CornerInsetFraction;
        double[] positionsX = [rootBounds.Min.X + insetX, rootBounds.Max.X - insetX];
        double[] positionsZ = [rootBounds.Min.Z + insetZ, rootBounds.Max.Z - insetZ];
        var corners = new WorldPosition[positionsX.Length * positionsZ.Length];
        int count = 0;
        for (int x = 0; x < positionsX.Length; x++)
        {
            for (int z = 0; z < positionsZ.Length; z++)
            {
                corners[count++] = new WorldPosition(
                    positionsX[x],
                    EyeHeight(rootBounds, surfaceHeight, positionsX[x], positionsZ[z]),
                    positionsZ[z]);
            }
        }

        return corners
            .OrderBy(corner => DistanceSquared(corner, authoredPosition))
            .ToArray();
    }

    /// <summary>
    /// Squared distance between two world positions. Comparing squared distances keeps the corner
    /// order away from a square root, which would only add rounding to a decision that has to stay
    /// deterministic frame to frame.
    /// </summary>
    private static double DistanceSquared(WorldPosition left, WorldPosition right)
    {
        double deltaX = left.X - right.X;
        double deltaY = left.Y - right.Y;
        double deltaZ = left.Z - right.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
    }

    /// <summary>
    /// Rotation that aims <paramref name="from"/> at the terrain bounds centre with the elevation
    /// clamped into <see cref="MinimumElevationDegrees"/>..<see cref="MaximumElevationDegrees"/>.
    /// </summary>
    public static Quaternion Aim(
        in TerrainPatchWorldBounds rootBounds,
        WorldPosition from)
    {
        if (!rootBounds.IsValid)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera path requires valid terrain root bounds.");
        }

        if (!from.IsFinite)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera path requires a finite camera position.");
        }

        WorldPosition target = Center(rootBounds);
        double deltaX = target.X - from.X;
        double deltaY = target.Y - from.Y;
        double deltaZ = target.Z - from.Z;
        double horizontalSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
        double lengthSquared = horizontalSquared + (deltaY * deltaY);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0.0)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera aim direction is degenerate.");
        }

        double length = Math.Sqrt(lengthSquared);
        double elevation = Math.Asin(Math.Clamp(deltaY / length, -1.0, 1.0));
        double clampedElevation = Math.Clamp(
            elevation,
            MinimumElevationDegrees * (Math.PI / 180.0),
            MaximumElevationDegrees * (Math.PI / 180.0));
        float yaw = double.IsFinite(horizontalSquared) &&
            horizontalSquared > DegenerateHorizontalEpsilon
            ? (float)Math.Atan2(deltaX, deltaZ)
            : 0.0f;
        return Quaternion.CreateFromYawPitchRoll(yaw, (float)(-clampedElevation), 0.0f);
    }

    /// <summary>
    /// Rotation whose render forward points from <paramref name="from"/> at
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// The render camera resolves its basis from the transform rotation as
    /// <c>Vector3.Transform(MathExtensions.Forward, rotation)</c> after a yaw/pitch/roll
    /// decomposition, which makes its forward
    /// <c>(sin(yaw) * cos(pitch), -sin(pitch), cos(yaw) * cos(pitch))</c>. The matching inverse
    /// therefore yaws about Y and pitches about X.
    /// </remarks>
    public static Quaternion LookRotation(WorldPosition from, WorldPosition target)
    {
        double deltaX = target.X - from.X;
        double deltaY = target.Y - from.Y;
        double deltaZ = target.Z - from.Z;
        double lengthSquared = (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0.0)
        {
            throw new InvalidOperationException(
                "Terrain-streaming camera look direction is degenerate.");
        }

        double length = Math.Sqrt(lengthSquared);
        float forwardX = (float)(deltaX / length);
        float forwardY = (float)(deltaY / length);
        float forwardZ = (float)(deltaZ / length);
        if (forwardY > 1.0f)
        {
            forwardY = 1.0f;
        }
        else if (forwardY < -1.0f)
        {
            forwardY = -1.0f;
        }

        float yaw = MathF.Atan2(forwardX, forwardZ);
        float pitch = -MathF.Asin(forwardY);
        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);
    }

    public static Vector3 Forward(in Quaternion rotation) =>
        Vector3.Transform(Vector3.UnitZ, rotation);

    public static float ElevationDegrees(in Quaternion rotation) =>
        MathF.Asin(Math.Clamp(Forward(rotation).Y, -1.0f, 1.0f)) * (180.0f / MathF.PI);

    /// <summary>
    /// Camera height of a derived pose: the queried surface height at the pose's own horizontal
    /// position plus <see cref="EyeClearanceMetres"/>. When the runtime query cannot answer - the
    /// fixture builds its path once during discovery, before the canonical tiles are active - the
    /// pose falls back to the highest point of the terrain plus the clearance. The bounds maximum is
    /// above every point of the surface, so a pose built from the fallback is never inside the
    /// terrain either; the fixture rebuilds the path as soon as the complete render snapshot exists.
    /// </summary>
    private static double EyeHeight(
        in TerrainPatchWorldBounds rootBounds,
        TerrainSurfaceHeightSampler? surfaceHeight,
        double worldX,
        double worldZ)
    {
        double sampled = surfaceHeight == null
            ? double.NaN
            : surfaceHeight(worldX, worldZ);
        double surface = double.IsFinite(sampled) ? sampled : rootBounds.Max.Y;
        return surface + EyeClearanceMetres;
    }

    private static WorldPosition Center(in TerrainPatchWorldBounds bounds) => new(
        (bounds.Min.X + bounds.Max.X) * 0.5,
        (bounds.Min.Y + bounds.Max.Y) * 0.5,
        (bounds.Min.Z + bounds.Max.Z) * 0.5);
}