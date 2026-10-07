using System.Numerics;
using JoltPhysicsSharp;
using Skeleton = Zombies.Engine.Animation.Skeleton;
using Bone = Zombies.Engine.Animation.Bone;
using Zombies.Engine.Physics.Gore;

namespace Zombies.Engine.Physics;

/// <summary>
/// The thin Jolt end of ragdolls: it builds dynamic bodies from a <see cref="RagdollPlan"/> and joins each one to its parent bone. What
/// the bodies are, where they start, and how hard they are pushed is decided in <see cref="RagdollPlan"/> and tested there. Ragdolls
/// live only on the client, so they never touch a player's character or anything the Server decides.
/// </summary>
public sealed partial class PhysicsWorld
{
    /// <summary>The most ragdoll bodies alive at once. The oldest group is removed to stay under it.</summary>
    public const int MaxRagdollBodies = 1024;

    private const float MinHalfExtent = 0.04f;

    private readonly Queue<RagdollGroupHandle> _ragdolls = new();

    private sealed record RagdollGroupHandle(List<BodyID> Bodies, List<Constraint> Constraints, List<Shape> Shapes);

    /// <summary>Ragdoll bodies in the physics world right now.</summary>
    public int RagdollBodyCount { get; private set; }

    /// <summary>Box shapes held by live ragdolls. Each is freed with its group, so this stays bounded by <see cref="MaxRagdollBodies"/>.</summary>
    public int RagdollShapeCount { get; private set; }

    /// <summary>Makes a body per entry of the plan, jointed to its parent, and returns how many it made. Not covered by tests: it needs Jolt's native library to run.</summary>
    public int AddRagdoll(IReadOnlyList<RagdollBodySpec> bodies, Skeleton skeleton, RigPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(skeleton);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bodies.Count == 0)
        {
            return 0;
        }

        while (RagdollBodyCount + bodies.Count > MaxRagdollBodies && _ragdolls.Count > 0)
        {
            RemoveRagdoll(_ragdolls.Dequeue());
        }

        var interfaces = _system.BodyInterface;
        var handle = new RagdollGroupHandle([], [], []);
        var made = new List<Body>(bodies.Count);
        foreach (var spec in bodies)
        {
            var half = HalfExtents(skeleton.Bones[spec.Bone], placement.Scale);
            var shape = new BoxShape(half);
            _shapes.Add(shape);
            handle.Shapes.Add(shape);
            RagdollShapeCount++;
            var settings = new BodyCreationSettings(shape, spec.Position, spec.Rotation, MotionType.Dynamic, new ObjectLayer(DebrisLayer));
            var body = interfaces.CreateBody(settings);
            interfaces.AddBody(body, Activation.Activate);
            interfaces.SetLinearVelocity(body.ID, spec.LinearVelocity);
            interfaces.SetAngularVelocity(body.ID, spec.AngularVelocity);
            handle.Bodies.Add(body.ID);
            made.Add(body);
        }

        for (var i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].Parent < 0)
            {
                continue;
            }

            // A ball joint at the child bone's pivot, which is where it hangs from its parent.
            var joint = new PointConstraintSettings
            {
                Space = ConstraintSpace.WorldSpace,
                Point1 = bodies[i].Position,
                Point2 = bodies[i].Position,
            };
            var constraint = joint.CreateConstraint(made[bodies[i].Parent], made[i]);
            _system.AddConstraint(constraint);
            handle.Constraints.Add(constraint);
        }

        _ragdolls.Enqueue(handle);
        RagdollBodyCount += bodies.Count;
        return bodies.Count;
    }

    private static Vector3 HalfExtents(Bone bone, Vector3 scale)
    {
        if (bone.Boxes.Count == 0)
        {
            return new Vector3(MinHalfExtent);
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var box in bone.Boxes)
        {
            min = Vector3.Min(min, box.MinMeters);
            max = Vector3.Max(max, box.MinMeters + box.SizeMeters);
        }

        return Vector3.Max(((max - min) * scale) / 2f, new Vector3(MinHalfExtent));
    }

    private void RemoveRagdoll(RagdollGroupHandle handle)
    {
        foreach (var constraint in handle.Constraints)
        {
            _system.RemoveConstraint(constraint);
            constraint.Dispose();
        }

        foreach (var id in handle.Bodies)
        {
            _system.BodyInterface.RemoveAndDestroyBody(id);
        }

        // The bodies are gone, so nothing uses the shapes any more. Same order as Dispose: bodies first, then shapes.
        foreach (var shape in handle.Shapes)
        {
            _shapes.Remove(shape);
            shape.Dispose();
        }

        RagdollShapeCount -= handle.Shapes.Count;
        RagdollBodyCount -= handle.Bodies.Count;
    }

    private void ClearRagdolls()
    {
        while (_ragdolls.Count > 0)
        {
            RemoveRagdoll(_ragdolls.Dequeue());
        }
    }
}
