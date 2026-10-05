using System.Numerics;

namespace Zombies.Engine.Animation;

/// <summary>
/// A position and a rotation, in meters and radians. Y is up and no rotation faces -Z, the same as the player's yaw of 0.
/// A bone's transform is relative to its parent until <see cref="RigPose"/> composes it into the rig's space.
/// </summary>
public readonly record struct BoneTransform(Vector3 Position, Quaternion Rotation)
{
    public static BoneTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>This transform, which is expressed in the space of <paramref name="parent"/>, expressed in the space the parent is in.</summary>
    public BoneTransform InParent(BoneTransform parent) =>
        new(Vector3.Transform(Position, parent.Rotation) + parent.Position, Quaternion.Normalize(parent.Rotation * Rotation));

    /// <summary>The transform that undoes this one.</summary>
    public BoneTransform Inverse()
    {
        var inverse = Quaternion.Conjugate(Rotation);
        return new BoneTransform(-Vector3.Transform(Position, inverse), inverse);
    }

    public Vector3 TransformPoint(Vector3 point) => Vector3.Transform(point, Rotation) + Position;

    public Vector3 TransformDirection(Vector3 direction) => Vector3.Transform(direction, Rotation);

    public static BoneTransform Lerp(BoneTransform start, BoneTransform end, float t) =>
        new(Vector3.Lerp(start.Position, end.Position, t), Quaternion.Slerp(start.Rotation, end.Rotation, t));

    /// <summary>A rotation from angles in degrees about X (pitch), Y (yaw), and Z (roll), the way rig data states them.</summary>
    public static Quaternion FromEulerDegrees(Vector3 degrees) =>
        Quaternion.CreateFromYawPitchRoll(
            degrees.Y * MathF.PI / 180f,
            degrees.X * MathF.PI / 180f,
            degrees.Z * MathF.PI / 180f);
}
