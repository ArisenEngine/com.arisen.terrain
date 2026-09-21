using System.Numerics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Terrain;

/// <summary>
/// Camera poses the terrain-streaming smoke fixture derives from the loaded terrain bounds.
/// </summary>
internal readonly record struct TerrainStreamingCameraPoses(
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
/// all four canonical tiles; the visual capture rejects a frame that writes no depth, so the frame
/// has to contain the terrain surface rather than only sky; and the summary validator rejects an
/// inverted frame whose ground sits above the horizon in the image, so the surface has to stay
/// under a view that keeps the sky on top.
///
/// Authored content cannot satisfy those contracts outside the exact pose the scene author framed.
/// The authored rotation culls the tile behind the view direction, and the authored height is only
/// above the surface at the authored position: a derived pose that inherits it walks into the
/// hillside, captures the inside of the terrain through back-face culling, and reads back a frame
/// whose lower part is missing depth.
///
/// The fixture therefore derives its own poses from the loaded terrain bounds and the runtime
/// surface query. Every derived pose stands <see cref="EyeClearanceMetres"/> above the terrain
/// surface at its own horizontal position, and every pose aims at the bounds centre with the
/// elevation clamped into a shallow downward band, which keeps the sky above the ridge line and
/// the streaming surface under the view in the same frame.
/// </summary>
internal static class TerrainStreamingCameraPath
{
    /// <summary>Distance the far checkpoint retreats from the authored camera position.</summary>
    public const float FarRetreat = 48.0f;

    /// <summary>
    /// Fraction of the terrain's X extent that the boundary checkpoint stands inside the terrain,
    /// measured from the eastern edge along the shared tile boundary in Z. The pose cannot stand on
    /// the four-tile corner: the aim direction to the bounds centre is horizontal there, which is
    /// degenerate. Standing on a tile boundary inside the terrain keeps both neighbouring tiles
    /// equally close, which is the mixed-LOD view the checkpoint exists for, and the eastern half of
    /// the boundary line keeps the pose on the valley floor where the surface stays under the view.
    /// </summary>
    public const float BoundaryInsetFraction = 0.25f;

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

        WorldPosition target = Center(rootBounds);
        Quaternion nearRotation = Aim(rootBounds, authoredPosition);
        double inset = (rootBounds.Max.X - rootBounds.Min.X) * BoundaryInsetFraction;
        double boundaryX = rootBounds.Max.X - inset;
        var boundaryPosition = new WorldPosition(
            boundaryX,
            EyeHeight(rootBounds, surfaceHeight, boundaryX, target.Z),
            target.Z);
        Vector3 forward = HorizontalForward(nearRotation);
        double farX = authoredPosition.X - (forward.X * FarRetreat);
        double farZ = authoredPosition.Z - (forward.Z * FarRetreat);
        var farPosition = new WorldPosition(
            farX,
            EyeHeight(rootBounds, surfaceHeight, farX, farZ),
            farZ);
        return new TerrainStreamingCameraPoses(
            nearRotation,
            boundaryPosition,
            Aim(rootBounds, boundaryPosition),
            farPosition,
            Aim(rootBounds, farPosition));
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

    /// <summary>
    /// View forward projected onto the terrain plane and normalized, so a derived pose retreats
    /// across the surface instead of walking down the view direction into the hillside.
    /// </summary>
    public static Vector3 HorizontalForward(in Quaternion rotation)
    {
        Vector3 forward = Forward(rotation);
        var horizontal = new Vector3(forward.X, 0.0f, forward.Z);
        return horizontal.LengthSquared() > 1.0e-6f
            ? Vector3.Normalize(horizontal)
            : Vector3.UnitZ;
    }

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