using System.Numerics;
using Zombies.Engine.Animation;
using Zombies.Engine.Physics;
using Zombies.Engine.Physics.Gore;
using Zombies.Engine.Tests.Rigs;

namespace Zombies.Engine.Tests;

/// <summary>Checks that ragdoll shapes are freed with their group, so the body cap also bounds shape memory. Runs Jolt natively.</summary>
public sealed class RagdollShapeTests
{
    [Fact]
    public void AddingFarMoreRagdollsThanTheCap_KeepsLiveShapesBounded()
    {
        var skeleton = RigTestData.Mini();
        var placement = new RigPlacement(new Vector3(0f, 80f, 0f), 0f, Vector3.One);
        var bodies = skeleton.Bones
            .Select((bone, i) => new RagdollBodySpec(i, bone.Parent, default, bone.Part, new Vector3(0f, 80f + i, 0f), Quaternion.Identity, Vector3.Zero, Vector3.Zero))
            .ToList();

        using var world = new PhysicsWorld();
        var groups = (PhysicsWorld.MaxRagdollBodies / bodies.Count) * 5;
        for (var i = 0; i < groups; i++)
        {
            Assert.Equal(bodies.Count, world.AddRagdoll(bodies, skeleton, placement));
        }

        Assert.True(world.RagdollBodyCount <= PhysicsWorld.MaxRagdollBodies);
        Assert.Equal(world.RagdollBodyCount, world.RagdollShapeCount);
    }
}
