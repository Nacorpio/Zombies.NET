using System.Numerics;

namespace Zombies.Engine.Render;

/// <summary>
/// A first-person or fly camera. World axes: +X east, +Y up, -Z north, so yaw 0 looks north and a positive yaw turns right.
/// The projection follows Vulkan's conventions: depth runs 0 to 1 and clip-space Y points down.
/// </summary>
public sealed class Camera
{
    private const float MaxPitch = 89f * MathF.PI / 180f;

    public Vector3 Position { get; set; }

    /// <summary>Turn around the vertical axis, in radians. Positive turns to the right.</summary>
    public float Yaw { get; set; }

    /// <summary>Look up (positive) or down (negative), in radians, clamped just short of straight up or down.</summary>
    public float Pitch
    {
        get;
        set => field = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    /// <summary>Vertical field of view in radians.</summary>
    public float FovY { get; set; } = 70f * MathF.PI / 180f;

    public float Near { get; set; } = 0.1f;

    public float Far { get; set; } = 400f;

    public float Aspect { get; set; } = 16f / 9f;

    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 Projection
    {
        get
        {
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, Near, Far);
            projection.M22 = -projection.M22;
            return projection;
        }
    }

    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>Turns the camera by a mouse movement. <paramref name="radiansPerPixel"/> is the sensitivity.</summary>
    public void Look(float deltaX, float deltaY, float radiansPerPixel = 0.0025f)
    {
        Yaw += deltaX * radiansPerPixel;
        Pitch -= deltaY * radiansPerPixel;
    }

    /// <summary>Flies relative to where the camera looks: x right, y up, z forward, in blocks.</summary>
    public void Fly(float right, float up, float forward)
    {
        var flat = Vector3.Normalize(new Vector3(Forward.X, 0, Forward.Z));
        Position += (Right * right) + (Vector3.UnitY * up) + (flat * forward);
    }
}
