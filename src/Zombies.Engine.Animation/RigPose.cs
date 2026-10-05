namespace Zombies.Engine.Animation;

/// <summary>
/// Where every bone of a skeleton is right now. An <see cref="Animator"/> fills it in each frame; a renderer reads
/// <see cref="World"/> to place each bone's mesh and <see cref="TryGetAttachPoint"/> to place whatever is held or worn.
/// One pose is reused from frame to frame, so animating allocates nothing.
/// </summary>
public sealed class RigPose
{
    private readonly BoneTransform[] _local;
    private readonly BoneTransform[] _world;
    private readonly bool[] _visible;

    public RigPose(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        var count = skeleton.Bones.Count;
        _local = new BoneTransform[count];
        _world = new BoneTransform[count];
        _visible = new bool[count];
        Reset(MissingPartSet.None);
        UpdateWorld();
    }

    public Skeleton Skeleton { get; }

    public int BoneCount => _local.Length;

    /// <summary>A bone's transform relative to its parent.</summary>
    public BoneTransform Local(int bone) => _local[bone];

    /// <summary>A bone's transform in the rig's space: meters from the rig's origin, which is at the feet for a body.</summary>
    public BoneTransform World(int bone) => _world[bone];

    /// <summary>Whether a bone is drawn. A bone whose Body part is a Missing part is not, and neither are the bones below it that take their part from it.</summary>
    public bool IsVisible(int bone) => _visible[bone];

    public BoneTransform World(string bone) => _world[IndexOf(bone)];

    /// <summary>
    /// Where an attach point is in the rig's space. False when the skeleton has no such attach point or the bone it is on is
    /// not drawn, because a hand that was shot off holds nothing.
    /// </summary>
    public bool TryGetAttachPoint(string name, out BoneTransform world)
    {
        world = BoneTransform.Identity;
        if (!Skeleton.TryGetAttachPoint(name, out var point) || !_visible[point.Bone])
        {
            return false;
        }

        world = point.Local.InParent(_world[point.Bone]);
        return true;
    }

    internal void SetLocal(int bone, BoneTransform transform) => _local[bone] = transform;

    /// <summary>Puts every bone back at rest and hides the ones that belong to a Missing part.</summary>
    internal void Reset(MissingPartSet missing)
    {
        for (var i = 0; i < _local.Length; i++)
        {
            _local[i] = Skeleton.Bones[i].Rest;
            _visible[i] = Skeleton.PartOf(i) is not { } part || !missing.Contains(part);
        }
    }

    /// <summary>Composes the local transforms down the tree. Parents come before their children, so one pass is enough.</summary>
    internal void UpdateWorld()
    {
        for (var i = 0; i < _local.Length; i++)
        {
            var parent = Skeleton.Bones[i].Parent;
            _world[i] = parent < 0 ? _local[i] : _local[i].InParent(_world[parent]);
        }
    }

    private int IndexOf(string bone)
    {
        var index = Skeleton.IndexOf(bone);
        return index >= 0 ? index : throw new ArgumentException($"The skeleton has no bone '{bone}'.", nameof(bone));
    }
}
