using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Survival;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Fatigue, sleep, and waking, driven through a Server and fake clients over the in-memory transport.</summary>
public sealed class SleepTests
{
    private const string Tired = "base:status_effect/tired";
    private const string Exhausted = "base:status_effect/exhausted";

    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private static readonly Vector3 BedAt = new(40, 80, 8);

    private static readonly StatusEffectCatalog Effects = new(
    [
        new StatusEffectDefinition(Tired, EffectCategory.Ailment, modifiers: [new EffectModifier(new StatName("handling"), ModifierOperation.Multiply, 0.9)]),
        new StatusEffectDefinition(Exhausted, EffectCategory.Ailment, modifiers: [new EffectModifier(new StatName("handling"), ModifierOperation.Multiply, 0.7)]),
    ]);

    /// <summary>A bed within a few blocks of <see cref="BedAt"/>, and open ground everywhere else.</summary>
    private sealed class OneBed : IRestPlaces
    {
        public RestPlace At(Vector3 position) => Vector3.Distance(position, BedAt) < 3 ? RestPlace.Bed : RestPlace.Ground;
    }

    // A day lasts four seconds, so every tick is 12 minutes of game time and fatigue grows 0.0125 a tick.
    private static ServerOptions Options() => new(Identity, WorldSeed: 7)
    {
        DayLength = TimeSpan.FromSeconds(4),
        StatusEffects = Effects,
        RestPlaces = new OneBed(),
    };

    private sealed class Rig
    {
        public Rig()
        {
            Server = new GameServer(Network.CreateServer(), Options());
        }

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public List<GameClient> Clients { get; } = [];

        public long Tick { get; private set; }

        public GameClient Join(string name)
        {
            var client = new GameClient(Network.Connect(), Identity, name);
            Clients.Add(client);
            return client;
        }

        public PlayerSession Session(int connection)
        {
            Assert.True(Server.TryGetPlayer(new ConnectionId(connection), out var session));
            return session;
        }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(Tick++);
                foreach (var client in Clients)
                {
                    client.Poll();
                }
            }
        }

        /// <summary>Lets the player grow tired by running until their fatigue passes <paramref name="fatigue"/>, then settles the network.</summary>
        public void Tire(PlayerSession session, double fatigue)
        {
            while (session.Needs.Fatigue < fatigue)
            {
                Run(1);
            }

            Run(3);
        }
    }

    private static PlayerState SeenBy(GameClient client, GameClient of)
    {
        Assert.True(client.World.TryGet(of.PlayerEntityId, out var entity));
        return entity.Player;
    }

    [Fact]
    public void FatigueGrowsWithGameTime_AndEachLevelAppliesItsStatusEffect()
    {
        var rig = new Rig();
        rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);

        Assert.False(session.Effects.Has(Tired));

        rig.Tire(session, 0.55);
        Assert.Equal(FatigueLevel.Tired, session.Needs.Tiredness);
        Assert.True(session.Effects.Has(Tired));
        Assert.True(session.Effects.EffectiveValue(new StatName("handling"), 100) < 100);

        rig.Tire(session, 0.8);
        Assert.False(session.Effects.Has(Tired));
        Assert.True(session.Effects.Has(Exhausted));
        Assert.Equal(70, session.Effects.EffectiveValue(new StatName("handling"), 100), 6);
    }

    [Fact]
    public void ASleepCommand_FromARestedPlayer_IsRejected()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);

        alice.Send(new SleepCommand(true));
        rig.Run(3);

        Assert.False(session.Needs.IsSleeping);
        Assert.Equal(CommandRejection.Invalid, alice.LastRejection?.Reason);
    }

    [Fact]
    public void ASleepCommand_FromATiredPlayer_PutsThemToSleep_AndEveryoneSeesIt()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);

        alice.Send(new SleepCommand(true));
        rig.Run(3);

        Assert.True(session.Needs.IsSleeping);
        Assert.Null(alice.LastRejection);
        Assert.True(SeenBy(bob, alice).Sleeping);
        Assert.True(SeenBy(alice, alice).Sleeping);
        Assert.False(SeenBy(bob, alice).Dead);
    }

    [Fact]
    public void ASleepingPlayer_CanGetUp_AndEveryoneSeesThat()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);

        alice.Send(new SleepCommand(false));
        rig.Run(3);

        Assert.False(session.Needs.IsSleeping);
        Assert.False(SeenBy(bob, alice).Sleeping);
    }

    [Fact]
    public void WakingAnAwakePlayer_IsRejected()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);

        alice.Send(new SleepCommand(false));
        rig.Run(3);

        Assert.Equal(CommandRejection.Invalid, alice.LastRejection?.Reason);
    }

    [Fact]
    public void ASleepingPlayer_CannotActOrMove()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var before));

        alice.Send(new MovePlayer(before.Position + new Vector3(1, 0, 0), 0));
        rig.Run(3);

        Assert.Equal(CommandRejection.Invalid, alice.LastRejection?.Reason);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var after));
        Assert.Equal(before.Position, after.Position);
    }

    [Fact]
    public void ASleepingPlayer_StaysWhereTheyLieWhenTheClientKeepsSendingInput()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var before));

        alice.Send(new PlayerInputCommand(new PlayerInput(1, 0, 0, 0, false, false, false, false, false)));
        rig.Run(3);

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var after));
        Assert.Equal(before.Position, after.Position);
        Assert.Null(alice.LastRejection);
    }

    [Fact]
    public void ASleeper_InABed_RecoversFasterThanOneOnTheGround()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var onGround = rig.Session(1);
        var inBed = rig.Session(2);
        Assert.True(rig.Server.Teleport(inBed, BedAt, 0f));
        rig.Tire(onGround, 0.6);

        alice.Send(new SleepCommand(true));
        bob.Send(new SleepCommand(true));
        rig.Run(3);
        var groundBefore = onGround.Needs.Fatigue;
        var bedBefore = inBed.Needs.Fatigue;
        rig.Run(5);

        Assert.True(onGround.Needs.IsSleeping);
        Assert.True(inBed.Needs.IsSleeping);
        Assert.True(groundBefore - onGround.Needs.Fatigue > 0);
        Assert.True(bedBefore - inBed.Needs.Fatigue > groundBefore - onGround.Needs.Fatigue);
    }

    [Fact]
    public void Sleeping_RemovesTheTirednessEffect_OnceTheyAreRestedEnough()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        Assert.True(session.Effects.Has(Tired));

        alice.Send(new SleepCommand(true));
        rig.Run(20);

        Assert.False(session.Effects.Has(Tired));
        Assert.Equal(FatigueLevel.Rested, session.Needs.Tiredness);
    }

    [Fact]
    public void ASleeper_WhoIsFullyRested_WakesUpAndIsSeenAwake()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));

        rig.Run(60);

        Assert.False(session.Needs.IsSleeping);
        Assert.False(SeenBy(bob, alice).Sleeping);
    }

    [Fact]
    public void AnExhaustedPlayer_CollapsesAsleep_WithoutAskingToSleep()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var session = rig.Session(1);

        rig.Tire(session, 1);

        Assert.True(session.Needs.IsSleeping);
        Assert.True(SeenBy(bob, alice).Sleeping);
    }

    [Fact]
    public void ANoise_WithinTheSleepersHearing_WakesThem_AndOneBeyondDoesNot()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var sleeper = rig.Session(1);
        var watcher = rig.Session(2);
        rig.Tire(sleeper, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);
        var spawn = rig.Server.Options.SpawnPoint;

        // A sleeper hears half as far as a noise carries.
        rig.Server.MakeNoise(spawn + new Vector3(30, 0, 0), loudness: 40);
        rig.Run(3);
        Assert.True(sleeper.Needs.IsSleeping);

        rig.Server.MakeNoise(spawn + new Vector3(15, 0, 0), loudness: 40);
        rig.Run(3);

        Assert.False(sleeper.Needs.IsSleeping);
        Assert.False(SeenBy(bob, alice).Sleeping);
        Assert.False(watcher.Needs.IsSleeping);
    }

    [Fact]
    public void ADamagedSleeper_WakesUp()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);

        rig.Server.Damage(session, BodyPart.LeftArm, DamageType.Cut, 5);
        rig.Run(3);

        Assert.False(session.Needs.IsSleeping);
        Assert.False(SeenBy(bob, alice).Sleeping);
    }

    [Fact]
    public void ARespawnedPlayer_IsAwakeAndRested()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);
        var session = rig.Session(1);
        rig.Tire(session, 0.55);
        alice.Send(new SleepCommand(true));
        rig.Run(3);

        rig.Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1000);
        rig.Run(320);

        Assert.False(session.IsDead);
        Assert.False(session.Needs.IsSleeping);
        Assert.Equal(FatigueLevel.Rested, session.Needs.Tiredness);
        Assert.False(session.Effects.Has(Tired));
    }
}
