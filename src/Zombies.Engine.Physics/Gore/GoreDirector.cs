using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Net;

namespace Zombies.Engine.Physics.Gore;

/// <summary>A zombie as the client last drew it: the pose, where it stands, and which parts it had already lost. What a ragdoll starts from.</summary>
public sealed record ZombieLook(Skeleton Skeleton, RigPose Pose, RigPlacement Placement, MissingPartSet Missing);

/// <summary>
/// The client's side of Cosmetic events. It turns each event into decals in a capped <see cref="DecalPool"/> and queues sprays and
/// ragdoll requests for the renderer and the physics world to take. It only reads events and holds only client state, so the gore
/// level can never change what the Server decides.
/// </summary>
public sealed class GoreDirector
{
    public const int DefaultDecalCapacity = 512;

    private readonly List<SprayBurst> _sprays = [];
    private readonly List<IReadOnlyList<RagdollBodySpec>> _bodies = [];

    public GoreDirector(GoreLevel level, int decalCapacity = DefaultDecalCapacity)
    {
        Level = level;
        Decals = new DecalPool(decalCapacity);
    }

    public GoreLevel Level { get; set; }

    public DecalPool Decals { get; }

    /// <summary>Sprays emitted since the renderer last took them.</summary>
    public IReadOnlyList<SprayBurst> PendingSprays => _sprays;

    /// <summary>
    /// Ragdolls, one list of bodies each (parent indices are within a list), waiting for <see cref="PhysicsWorld.AddRagdoll"/>: a corpse on death, a severed part when one is shot off. A ragdoll
    /// is movement and not gore, so a corpse is made at every level; <see cref="GoreProfile"/> only decides whether severed parts show.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<RagdollBodySpec>> PendingBodies => _bodies;

    /// <summary>Plays one Cosmetic event. <paramref name="look"/> is the zombie's last drawn pose, or null when the client has none, which skips the ragdoll.</summary>
    public void Play(ZombieCosmetic cosmetic, ZombieLook? look)
    {
        ArgumentNullException.ThrowIfNull(cosmetic);
        var effects = GoreEffects.For(cosmetic, GoreProfile.For(Level));
        foreach (var decal in effects.Decals)
        {
            Decals.Add(decal);
        }

        if (effects.Spray is { } spray)
        {
            _sprays.Add(spray);
        }

        if (look is not null)
        {
            var bodies = RagdollPlan.Build(look.Skeleton, look.Pose, look.Placement, cosmetic, look.Missing, GoreProfile.For(Level));
            if (bodies.Count > 0)
            {
                _bodies.Add(bodies);
            }
        }
    }

    public void ClearPending()
    {
        _sprays.Clear();
        _bodies.Clear();
    }
}
