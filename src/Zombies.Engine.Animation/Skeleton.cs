using System.Numerics;
using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Engine.Animation;

/// <summary>
/// One box of voxels in a bone's mesh. <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> is the corner nearest the origin
/// and the sizes count voxels, all relative to the bone's pivot. <see cref="U"/> and <see cref="V"/> are the pixel on the
/// character atlas where the box's unfolded faces start.
/// </summary>
public sealed record VoxelBox(int X, int Y, int Z, int Width, int Height, int Depth, int U, int V)
{
    public Vector3 MinMeters => new(VoxelScale.ToMeters(X), VoxelScale.ToMeters(Y), VoxelScale.ToMeters(Z));

    public Vector3 SizeMeters => new(VoxelScale.ToMeters(Width), VoxelScale.ToMeters(Height), VoxelScale.ToMeters(Depth));

    public int VoxelCount => Width * Height * Depth;
}

/// <summary>
/// One joint of a skeleton. <see cref="Rest"/> is where it sits in its parent with no animation. <see cref="Part"/> is the
/// Body part the bone belongs to, or null to belong to whatever its parent does, so a Missing part takes its whole limb with it.
/// </summary>
public sealed record Bone(string Name, int Parent, BoneTransform Rest, BodyPart? Part, IReadOnlyList<VoxelBox> Boxes);

/// <summary>A named place on a bone that something else is drawn at: a held item, a weapon, a backpack.</summary>
public sealed record AttachPoint(string Name, int Bone, BoneTransform Local);

/// <summary>
/// One back-and-forth swing the procedural walk adds to a bone. <see cref="Phase"/> is a fraction of a stride, so a leg and the
/// opposite leg differ by one half. <see cref="Frequency"/> is how many times the swing repeats per stride, so a bob that happens
/// at every footfall has a frequency of two. A swing marked <see cref="Upright"/> is for a character that can stand, and is
/// left out when it is crawling.
/// </summary>
public sealed record WalkSwing(int Bone, Vector3 RotationDegrees, Vector3 OffsetMeters, float Phase, float Frequency, bool Upright);

/// <summary>How the procedural walk moves a skeleton: how far one stride goes, the speed at which the swings are full size, and the swings.</summary>
public sealed record WalkSetup(float StrideMeters, float FullSpeed, IReadOnlyList<WalkSwing> Swings)
{
    public const float DefaultStrideMeters = 1.5f;

    public const float DefaultFullSpeed = 4.3f;

    public static WalkSetup None { get; } = new(DefaultStrideMeters, DefaultFullSpeed, []);
}

/// <summary>How much of the look direction one bone takes, and how far it may turn. The weights of a head and a torso add up to the whole turn.</summary>
public sealed record LookLink(int Bone, float YawWeight, float PitchWeight, float MaxYawRadians, float MaxPitchRadians);

/// <summary>
/// A generic skeleton read from JSON: bones in a tree, each with voxel boxes on the character atlas, the attach points other
/// things are drawn at, and the setup for the procedural walk and look-at. Nothing here is specific to a human, so a zombie, an
/// animal, and the first-person arms are all skeletons. Bones are listed parents first, which is what lets a pose be built in one pass.
/// </summary>
public sealed class Skeleton
{
    public const int MaxBones = 128;

    public const int MaxBoxesPerBone = 64;

    private readonly Dictionary<string, int> _boneIndex;
    private readonly Dictionary<string, AttachPoint> _attachPoints;
    private readonly BodyPart?[] _effectiveParts;

    private Skeleton(
        string id,
        List<Bone> bones,
        List<AttachPoint> attachPoints,
        WalkSetup walk,
        List<LookLink> lookAt)
    {
        Id = id;
        Bones = bones;
        AttachPoints = attachPoints;
        Walk = walk;
        LookAt = lookAt;
        _boneIndex = bones.Select((b, i) => (b.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        _attachPoints = attachPoints.ToDictionary(a => a.Name, StringComparer.Ordinal);
        _effectiveParts = new BodyPart?[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            _effectiveParts[i] = bones[i].Part ?? (bones[i].Parent >= 0 ? _effectiveParts[bones[i].Parent] : null);
        }
    }

    public string Id { get; }

    public IReadOnlyList<Bone> Bones { get; }

    public IReadOnlyList<AttachPoint> AttachPoints { get; }

    public WalkSetup Walk { get; }

    public IReadOnlyList<LookLink> LookAt { get; }

    /// <summary>The index of a bone by name, or -1.</summary>
    public int IndexOf(string name) => _boneIndex.GetValueOrDefault(name, -1);

    public bool TryGetAttachPoint(string name, out AttachPoint attachPoint)
    {
        if (_attachPoints.TryGetValue(name, out var found))
        {
            attachPoint = found;
            return true;
        }

        attachPoint = null!;
        return false;
    }

    /// <summary>The Body part a bone belongs to, taken from the nearest bone above it that names one, or null when none does.</summary>
    public BodyPart? PartOf(int bone) => _effectiveParts[bone];

    /// <summary>Every voxel of every bone's mesh, which is a rough measure of how much a body costs to draw.</summary>
    public int VoxelCount => Bones.Sum(b => b.Boxes.Sum(box => box.VoxelCount));

    public static bool TryParse(string json, out Skeleton skeleton, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        skeleton = null!;
        if (!RigJson.TryParseRoot(json, "skeleton", out var document, out error))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!RigJson.TryString(root, "id", "The skeleton", out var id, out error)
                || !RigJson.TryArray(root, "bones", "The skeleton", required: true, MaxBones, out var boneArray, out error))
            {
                return false;
            }

            if (boneArray.GetArrayLength() == 0)
            {
                error = "The skeleton needs at least one bone.";
                return false;
            }

            var bones = new List<Bone>();
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var element in boneArray.EnumerateArray())
            {
                if (!TryBone(element, bones.Count, names, out var bone, out error))
                {
                    return false;
                }

                names.Add(bone.Name, bones.Count);
                bones.Add(bone);
            }

            if (!TryAttachPoints(root, names, out var attachPoints, out error)
                || !TryWalk(root, names, out var walk, out error)
                || !TryLookAt(root, names, out var lookAt, out error))
            {
                return false;
            }

            skeleton = new Skeleton(id, bones, attachPoints, walk, lookAt);
            return true;
        }
    }

    private static bool TryBone(JsonElement element, int index, Dictionary<string, int> names, out Bone bone, out string error)
    {
        var path = $"bones[{index}]";
        bone = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{path} must be an object.";
            return false;
        }

        if (!RigJson.TryString(element, "name", path, out var name, out error)
            || !RigJson.TryOptionalString(element, "parent", path, out var parentName, out error)
            || !RigJson.TryVector(element, "offset", path, Vector3.Zero, out var offset, out error)
            || !RigJson.TryVector(element, "rotation", path, Vector3.Zero, out var rotation, out error)
            || !RigJson.TryOptionalString(element, "part", path, out var partName, out error))
        {
            return false;
        }

        if (names.ContainsKey(name))
        {
            error = $"{path}: the bone '{name}' is listed twice.";
            return false;
        }

        var parent = -1;
        if (parentName is not null && !names.TryGetValue(parentName, out parent))
        {
            error = $"{path}: the parent '{parentName}' of '{name}' must be a bone listed before it.";
            return false;
        }

        BodyPart? part = null;
        if (partName is not null)
        {
            if (!Enum.TryParse<BodyPart>(partName, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                error = $"{path}: '{partName}' is not a body part.";
                return false;
            }

            part = parsed;
        }

        if (!TryBoxes(element, path, out var boxes, out error))
        {
            return false;
        }

        var rest = new BoneTransform(offset * VoxelScale.VoxelMeters, BoneTransform.FromEulerDegrees(rotation));
        bone = new Bone(name, parent, rest, part, boxes);
        return true;
    }

    private static bool TryBoxes(JsonElement bone, string path, out List<VoxelBox> boxes, out string error)
    {
        boxes = [];
        if (!RigJson.TryArray(bone, "boxes", path, required: false, MaxBoxesPerBone, out var array, out error))
        {
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var boxPath = $"{path}.boxes[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"{boxPath} must be an object.";
                return false;
            }

            if (!RigJson.TryInts(element, "min", boxPath, 3, -RigJson.MaxVoxelCoordinate, out var min, out error)
                || !RigJson.TryInts(element, "size", boxPath, 3, 1, out var size, out error)
                || !RigJson.TryInts(element, "uv", boxPath, 2, 0, out var uv, out error))
            {
                return false;
            }

            boxes.Add(new VoxelBox(min[0], min[1], min[2], size[0], size[1], size[2], uv[0], uv[1]));
        }

        return true;
    }

    private static bool TryAttachPoints(JsonElement root, Dictionary<string, int> bones, out List<AttachPoint> attachPoints, out string error)
    {
        attachPoints = [];
        if (!RigJson.TryArray(root, "attachPoints", "The skeleton", required: false, MaxBones, out var array, out error))
        {
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"attachPoints[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"{path} must be an object.";
                return false;
            }

            if (!RigJson.TryString(element, "name", path, out var name, out error)
                || !RigJson.TryString(element, "bone", path, out var boneName, out error)
                || !RigJson.TryVector(element, "offset", path, Vector3.Zero, out var offset, out error)
                || !RigJson.TryVector(element, "rotation", path, Vector3.Zero, out var rotation, out error))
            {
                return false;
            }

            if (!seen.Add(name))
            {
                error = $"{path}: the attach point '{name}' is listed twice.";
                return false;
            }

            if (!bones.TryGetValue(boneName, out var bone))
            {
                error = $"{path}: the attach point '{name}' is on '{boneName}', which is not a bone.";
                return false;
            }

            attachPoints.Add(new AttachPoint(name, bone, new BoneTransform(offset * VoxelScale.VoxelMeters, BoneTransform.FromEulerDegrees(rotation))));
        }

        return true;
    }

    private static bool TryWalk(JsonElement root, Dictionary<string, int> bones, out WalkSetup walk, out string error)
    {
        walk = WalkSetup.None;
        error = string.Empty;
        if (!root.TryGetProperty("walk", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "walk must be an object.";
            return false;
        }

        if (!RigJson.TryNumber(element, "strideLength", "walk", WalkSetup.DefaultStrideMeters, 0.1f, 10f, out var stride, out error)
            || !RigJson.TryNumber(element, "fullSpeed", "walk", WalkSetup.DefaultFullSpeed, 0.1f, 100f, out var fullSpeed, out error)
            || !RigJson.TryArray(element, "swings", "walk", required: false, MaxBones, out var array, out error))
        {
            return false;
        }

        var swings = new List<WalkSwing>();
        if (array.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var swing in array.EnumerateArray())
            {
                var path = $"walk.swings[{index++}]";
                if (swing.ValueKind != JsonValueKind.Object)
                {
                    error = $"{path} must be an object.";
                    return false;
                }

                if (!RigJson.TryString(swing, "bone", path, out var boneName, out error)
                    || !RigJson.TryVector(swing, "rotation", path, Vector3.Zero, out var rotation, out error)
                    || !RigJson.TryVector(swing, "offset", path, Vector3.Zero, out var offset, out error)
                    || !RigJson.TryNumber(swing, "phase", path, 0f, 0f, 1f, out var phase, out error)
                    || !RigJson.TryNumber(swing, "frequency", path, 1f, 0.5f, 8f, out var frequency, out error)
                    || !RigJson.TryBool(swing, "upright", path, false, out var upright, out error))
                {
                    return false;
                }

                if (!bones.TryGetValue(boneName, out var bone))
                {
                    error = $"{path}: '{boneName}' is not a bone.";
                    return false;
                }

                swings.Add(new WalkSwing(bone, rotation, offset * VoxelScale.VoxelMeters, phase, frequency, upright));
            }
        }

        walk = new WalkSetup(stride, fullSpeed, swings);
        return true;
    }

    private static bool TryLookAt(JsonElement root, Dictionary<string, int> bones, out List<LookLink> lookAt, out string error)
    {
        lookAt = [];
        if (!RigJson.TryArray(root, "lookAt", "The skeleton", required: false, MaxBones, out var array, out error))
        {
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var index = 0;
        foreach (var link in array.EnumerateArray())
        {
            var path = $"lookAt[{index++}]";
            if (link.ValueKind != JsonValueKind.Object)
            {
                error = $"{path} must be an object.";
                return false;
            }

            if (!RigJson.TryString(link, "bone", path, out var boneName, out error)
                || !RigJson.TryNumber(link, "yawWeight", path, 0f, 0f, 1f, out var yawWeight, out error)
                || !RigJson.TryNumber(link, "pitchWeight", path, 0f, 0f, 1f, out var pitchWeight, out error)
                || !RigJson.TryNumber(link, "maxYaw", path, 90f, 0f, 180f, out var maxYaw, out error)
                || !RigJson.TryNumber(link, "maxPitch", path, 80f, 0f, 90f, out var maxPitch, out error))
            {
                return false;
            }

            if (!bones.TryGetValue(boneName, out var bone))
            {
                error = $"{path}: '{boneName}' is not a bone.";
                return false;
            }

            lookAt.Add(new LookLink(bone, yawWeight, pitchWeight, maxYaw * MathF.PI / 180f, maxPitch * MathF.PI / 180f));
        }

        return true;
    }
}
