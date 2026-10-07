using System.Numerics;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;
using Zombies.Engine.Animation;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Every client sees the same zombie as the Server: the same spec, the same Missing parts, the same death.</summary>
public sealed class ZombieReplicationTests
{
    private sealed class Rig
    {
        public Rig(int dropEveryNthUnreliable = 0)
        {
            var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
            Identity = GameIdentity.From(loaded, WorldGenerator.GeneratorVersion);
            Catalog = ZombieContentLoader.Load(loaded.Registry);
            Network.DropEveryNthUnreliable = dropEveryNthUnreliable;
            var transport = Network.CreateServer();
            Server = new GameServer(transport, new ServerOptions(Identity, WorldSeed: 5));
            var traits = new TraitRegistry();
            BaseTraits.Register(traits);
            Zombies = new ZombieSystem(Server.World, Catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
        }

        public GameIdentity Identity { get; }

        public ZombieCatalog Catalog { get; }

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public ZombieSystem Zombies { get; }

        public List<GameClient> Clients { get; } = [];

        public long Tick { get; private set; }

        public GameClient Join(string name)
        {
            var client = new GameClient(Network.Connect(), Identity, name);
            Clients.Add(client);
            return client;
        }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(Tick);
                Zombies.Tick(Tick);
                Tick++;
                foreach (var client in Clients)
                {
                    client.Poll();
                }
            }
        }

        public uint Spawn(string type, ulong seed, Vector3 at, int level = 2)
        {
            Assert.True(Zombies.TrySpawn(new ZombieSpec(seed, type, level), at, 0f, out var id, out var problem), problem);
            return id;
        }
    }

    /// <summary>
    /// Shoots at a zombie standing at <paramref name="at"/> from the front, at one height after another until a part is hit. A zombie
    /// that spawned without a leg crawls with its body low, so no single height is sure to hit.
    /// </summary>
    private static ZombieHit Shoot(Rig rig, Vector3 at, double damage = 1000)
    {
        for (var height = 0.2f; height < 2.0f; height += 0.2f)
        {
            if (rig.Zombies.Hit(new Vector3(at.X, at.Y + height, at.Z - 7), Vector3.UnitZ, 30f, DamageType.Cut, damage) is { } hit)
            {
                return hit;
            }
        }

        throw new InvalidOperationException("The zombie could not be hit anywhere.");
    }

    private static byte Bit(BodyPart part) => (byte)MissingParts.From(part);

    private static EntityState Zombie(GameClient client, uint id)
    {
        Assert.True(client.World.TryGet(id, out var state), $"The client does not see zombie {id}.");
        return state;
    }

    private static ZombieAppearance Derive(ZombieCatalog catalog, in ZombieState state)
    {
        var type = catalog.At(state.Type);
        return ZombieGenerator.Generate(type, new ZombieSpec(state.Seed, type.Id, state.Level), catalog.Weapons);
    }

    [Fact]
    public void EveryClient_SeesTheSameZombieTheServerSpawned()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        var ids = new[]
        {
            rig.Spawn("base:zombie/walker", 11, new Vector3(12, 80, 12)),
            rig.Spawn("base:zombie/runner", 12, new Vector3(14, 80, 12)),
            rig.Spawn("base:zombie/bloater", 13, new Vector3(16, 80, 12)),
        };

        rig.Run(60);

        foreach (var client in new[] { alice, bob })
        {
            Assert.Equal(rig.Server.World.Entities.ToArray(), client.World.Entities.ToArray());
            foreach (var id in ids)
            {
                Assert.True(rig.Server.World.TryGet(id, out var truth));
                var seen = Zombie(client, id);
                Assert.Equal(EntityKind.Zombie, seen.Kind);
                Assert.Equal(truth.Zombie, seen.Zombie);
            }
        }
    }

    [Fact]
    public void TheLookEachClientDerivesFromTheSpec_IsTheLookTheServerHas()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        var ids = Enumerable.Range(0, 12)
            .Select(i => rig.Spawn(rig.Catalog.At(i % rig.Catalog.Types.Count).Id, (ulong)(100 + i), new Vector3(10 + i, 80, 12)))
            .ToList();

        rig.Run(60);

        foreach (var id in ids)
        {
            Assert.True(rig.Zombies.TryGetAppearance(id, out var server));
            var onAlice = Derive(rig.Catalog, Zombie(alice, id).Zombie);
            var onBob = Derive(rig.Catalog, Zombie(bob, id).Zombie);

            Assert.Equal(server, onAlice);
            Assert.Equal(server, onBob);
            Assert.Equal((byte)MissingParts.From(server.MissingParts), Zombie(alice, id).Zombie.Missing);
        }
    }

    [Fact]
    public void ADismemberment_ReachesEveryClient_AndSoDoesTheDeath()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        var at = new Vector3(12, 80, 12);
        var id = rig.Spawn("base:zombie/walker", seed: 3, at);
        rig.Run(40);
        var before = Zombie(alice, id).Zombie.Missing;
        Assert.Equal(rig.Catalog.IndexOf("base:zombie/walker"), Zombie(alice, id).Zombie.Type);

        var hit = Shoot(rig, at, damage: 500);
        rig.Run(40);

        Assert.Equal(id, hit.Entity);
        Assert.True(hit.LostPart || hit.Killed);
        foreach (var client in new[] { alice, bob })
        {
            var seen = Zombie(client, id).Zombie;
            Assert.Equal(hit.Killed, seen.Dead);
            Assert.Equal(hit.LostPart ? (byte)(before | Bit(hit.Part)) : before, seen.Missing);
        }
    }

    [Fact]
    public void ALimbTakenOff_ShowsOnEveryClientAsItsBit()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var at = new Vector3(12, 80, 12);
        var id = rig.Spawn("base:zombie/runner", seed: 21, at);
        rig.Run(40);
        var before = Zombie(alice, id).Zombie.Missing;

        // The nearest part along a level shot at arm height from the right is the right arm, unless the zombie already lacks it.
        var hit = rig.Zombies.Hit(new Vector3(at.X + 5, at.Y + 1.0f, at.Z), -Vector3.UnitX, 30f, DamageType.Cut, 1000);
        rig.Run(40);

        Assert.NotNull(hit);
        Assert.True(hit.LostPart);
        Assert.Equal((byte)(before | Bit(hit.Part)), Zombie(alice, id).Zombie.Missing);
        Assert.False(Zombie(alice, id).Zombie.Dead);
    }

    [Fact]
    public void ALimbLostWhileTheClientHasAnOlderSnapshot_StillConverges_UnderPacketLoss()
    {
        var rig = new Rig(dropEveryNthUnreliable: 3);
        var alice = rig.Join("alice");
        var id = rig.Spawn("base:zombie/bloater", seed: 8, new Vector3(12, 80, 12));
        rig.Run(60);

        Shoot(rig, new Vector3(12, 80, 12));
        rig.Run(120);

        Assert.True(rig.Server.World.TryGet(id, out var truth));
        Assert.Equal(truth.Zombie, Zombie(alice, id).Zombie);
        Assert.True(truth.Zombie.Dead || truth.Zombie.Missing != 0);
    }

    [Fact]
    public void ARemovedCorpse_LeavesEveryClientsWorld()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var id = rig.Spawn("base:zombie/walker", seed: 4, new Vector3(12, 80, 12));
        rig.Run(40);
        Shoot(rig, new Vector3(12, 80, 12), damage: 100000);

        rig.Run(30 * 31);

        Assert.False(alice.World.TryGet(id, out _));
        Assert.Equal(rig.Server.World.Entities.ToArray(), alice.World.Entities.ToArray());
    }

    [Fact]
    public void AZombieOutsideAClientsInterest_IsNotSentToIt()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var near = rig.Spawn("base:zombie/walker", 1, new Vector3(12, 80, 12));
        var far = rig.Spawn("base:zombie/walker", 2, new Vector3(16 * 40, 80, 12));

        rig.Run(60);

        Assert.True(alice.World.TryGet(near, out _));
        Assert.False(alice.World.TryGet(far, out _));
    }

    [Fact]
    public void ANewClient_ThatJoinsLater_SeesTheZombiesAsTheyAreNow()
    {
        var rig = new Rig();
        var id = rig.Spawn("base:zombie/walker", seed: 6, new Vector3(12, 80, 12));
        rig.Run(5);
        var hit = Shoot(rig, new Vector3(12, 80, 12));
        var late = rig.Join("late");

        rig.Run(60);

        Assert.True(rig.Server.World.TryGet(id, out var truth));
        Assert.Equal(truth.Zombie, Zombie(late, id).Zombie);
        Assert.Equal(hit.Killed, Zombie(late, id).Zombie.Dead);
    }
}
