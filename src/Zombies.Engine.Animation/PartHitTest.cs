using System.Numerics;
using Zombies.Domain.Items;

namespace Zombies.Engine.Animation;

/// <summary>The Body part a ray hit, the bone and distance where it hit, and the point in world space.</summary>
public readonly record struct PartHit(BodyPart Part, int Bone, float Distance, Vector3 Point);

/// <summary>
/// Tests a ray against the boxes of a posed skeleton, one Body part at a time. The Server uses it on a kinematic pose instead of
/// a physics body per zombie (ADR 0007): the boxes are the ones the skeleton already draws, moved by the pose, so what a
/// player sees is what a shot hits. A bone of a Missing part is not drawn and so cannot be hit.
/// </summary>
public static class PartHitTest
{
    /// <summary>
    /// The nearest Body part a ray hits. The skeleton stands at <paramref name="position"/> facing <paramref name="yaw"/> (the
    /// player's yaw, where 0 faces -Z) and is stretched by <paramref name="scale"/> on each axis, which is how a zombie's height
    /// and build apply. <paramref name="direction"/> need not be a unit vector, and distances are in meters of the world.
    /// </summary>
    public static bool TryRaycast(
        RigPose pose,
        Vector3 position,
        float yaw,
        Vector3 scale,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out PartHit hit)
    {
        ArgumentNullException.ThrowIfNull(pose);
        hit = default;
        if (!IsFinite(position) || !IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(yaw) || !float.IsFinite(maxDistance)
            || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0 || direction.LengthSquared() < 1e-12f)
        {
            return false;
        }

        direction = Vector3.Normalize(direction);

        // Move the ray into the skeleton's own space, where the pose and boxes are stated; its direction is left unnormalized so a
        // distance along it is still a distance in the world.
        var undo = Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yaw));
        var rigOrigin = Vector3.Transform(origin - position, undo) / scale;
        var rigDirection = Vector3.Transform(direction, undo) / scale;

        var skeleton = pose.Skeleton;
        var nearest = float.MaxValue;
        var nearestBone = -1;
        for (var bone = 0; bone < pose.BoneCount; bone++)
        {
            if (skeleton.PartOf(bone) is null || !pose.IsVisible(bone))
            {
                continue;
            }

            var toBone = pose.World(bone).Inverse();
            var boneOrigin = toBone.TransformPoint(rigOrigin);
            var boneDirection = toBone.TransformDirection(rigDirection);
            foreach (var box in skeleton.Bones[bone].Boxes)
            {
                if (TryBox(boneOrigin, boneDirection, box.MinMeters, box.MinMeters + box.SizeMeters, out var t) && t >= 0 && t <= maxDistance && t < nearest)
                {
                    nearest = t;
                    nearestBone = bone;
                }
            }
        }

        if (nearestBone < 0)
        {
            return false;
        }

        hit = new PartHit(skeleton.PartOf(nearestBone)!.Value, nearestBone, nearest, origin + (direction * nearest));
        return true;
    }

    /// <summary>The distance at which a ray first enters a box (zero when it starts inside), by the slab method.</summary>
    private static bool TryBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f;
        var far = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = Component(origin, axis);
            var d = Component(direction, axis);
            var low = Component(min, axis);
            var high = Component(max, axis);
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < low || o > high)
                {
                    distance = 0;
                    return false;
                }

                continue;
            }

            var t1 = (low - o) / d;
            var t2 = (high - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
            {
                distance = 0;
                return false;
            }
        }

        distance = near;
        return true;
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
