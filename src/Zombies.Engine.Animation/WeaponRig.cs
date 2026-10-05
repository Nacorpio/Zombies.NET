using System.Numerics;
using System.Text.Json;

namespace Zombies.Engine.Animation;

/// <summary>
/// What a skeleton needs to hold a Weapon: where the weapon is gripped, and where each of its Mounts is, both in the weapon's
/// own space. Mounts are named like the Weapon's Mounts, such as muzzle or optic, so an Attachment fitted to a Mount is drawn
/// at the Mount of the same name. Read from JSON as <c>{ "id", "grip": { "offset", "rotation" }, "mounts": { "muzzle": { "offset", "rotation" } } }</c>
/// with offsets in voxels and rotations in degrees.
/// </summary>
public sealed class WeaponRig
{
    public const int MaxMounts = 32;

    public WeaponRig(string id, BoneTransform grip, IReadOnlyDictionary<string, BoneTransform> mounts)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(mounts);
        Id = id;
        Grip = grip;
        Mounts = new Dictionary<string, BoneTransform>(mounts, StringComparer.Ordinal);
    }

    public string Id { get; }

    /// <summary>The point of the weapon that sits in the hand. The weapon is placed so this lands on the skeleton's hold point.</summary>
    public BoneTransform Grip { get; }

    public IReadOnlyDictionary<string, BoneTransform> Mounts { get; }

    public static bool TryParse(string json, out WeaponRig rig, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        rig = null!;
        if (!RigJson.TryParseRoot(json, "weapon rig", out var document, out error))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!RigJson.TryString(root, "id", "The weapon rig", out var id, out error)
                || !TryPlacement(root, "grip", "The weapon rig", out var grip, out error))
            {
                return false;
            }

            var mounts = new Dictionary<string, BoneTransform>(StringComparer.Ordinal);
            if (root.TryGetProperty("mounts", out var mountsElement))
            {
                if (mountsElement.ValueKind != JsonValueKind.Object)
                {
                    error = "mounts must be an object.";
                    return false;
                }

                foreach (var mount in mountsElement.EnumerateObject())
                {
                    if (mounts.Count >= MaxMounts)
                    {
                        error = $"A weapon rig may not have more than {MaxMounts} mounts.";
                        return false;
                    }

                    if (mount.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = $"mounts.{mount.Name} must be an object.";
                        return false;
                    }

                    if (!TryTransform(mount.Value, $"mounts.{mount.Name}", out var transform, out error))
                    {
                        return false;
                    }

                    mounts[mount.Name] = transform;
                }
            }

            rig = new WeaponRig(id, grip, mounts);
            return true;
        }
    }

    private static bool TryPlacement(JsonElement owner, string name, string path, out BoneTransform transform, out string error)
    {
        transform = BoneTransform.Identity;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{path}: '{name}' must be an object.";
            return false;
        }

        return TryTransform(element, name, out transform, out error);
    }

    private static bool TryTransform(JsonElement element, string path, out BoneTransform transform, out string error)
    {
        transform = BoneTransform.Identity;
        if (!RigJson.TryVector(element, "offset", path, Vector3.Zero, out var offset, out error)
            || !RigJson.TryVector(element, "rotation", path, Vector3.Zero, out var rotation, out error))
        {
            return false;
        }

        transform = new BoneTransform(offset * VoxelScale.VoxelMeters, BoneTransform.FromEulerDegrees(rotation));
        return true;
    }
}

/// <summary>Where one Attachment is drawn: the Mount it is fitted to, and the Mount's place in the rig's space.</summary>
public sealed record MountPlacement(string Mount, BoneTransform World);

/// <summary>Where a held weapon and its Attachments are drawn, in the rig's space.</summary>
public sealed record HeldWeaponPlacement(BoneTransform Weapon, IReadOnlyList<MountPlacement> Attachments, IReadOnlyList<string> UnknownMounts);

/// <summary>
/// Puts a Held weapon and its Attachments on a posed skeleton. It needs only the skeleton's attach point and the weapon's own
/// rig, so any skeleton that names a hold point can carry any weapon: a player's hand, a zombie's, or the first-person arms.
/// </summary>
public static class HeldWeapon
{
    /// <summary>The attach point a skeleton names for what its right hand holds.</summary>
    public const string RightHand = "held_right";

    /// <summary>The attach point for the left hand, for a weapon held in the other hand or a second item.</summary>
    public const string LeftHand = "held_left";

    /// <summary>
    /// Places <paramref name="weapon"/> so its grip is at <paramref name="holdPoint"/>, and each Mount in
    /// <paramref name="fittedMounts"/> at the weapon's Mount of that name. A fitted Mount the weapon rig does not have is
    /// reported in <see cref="HeldWeaponPlacement.UnknownMounts"/> and not drawn. False when the skeleton has no such hold
    /// point or the hand it is on is a Missing part.
    /// </summary>
    public static bool TryPlace(RigPose pose, string holdPoint, WeaponRig weapon, IEnumerable<string> fittedMounts, out HeldWeaponPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(fittedMounts);
        placement = null!;
        if (!pose.TryGetAttachPoint(holdPoint, out var hold))
        {
            return false;
        }

        var weaponWorld = weapon.Grip.Inverse().InParent(hold);
        var attachments = new List<MountPlacement>();
        var unknown = new List<string>();
        foreach (var mount in fittedMounts)
        {
            if (weapon.Mounts.TryGetValue(mount, out var local))
            {
                attachments.Add(new MountPlacement(mount, local.InParent(weaponWorld)));
            }
            else
            {
                unknown.Add(mount);
            }
        }

        placement = new HeldWeaponPlacement(weaponWorld, attachments, unknown);
        return true;
    }
}
