using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;
using Zombies.Domain.Zombies;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Ui;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>
/// Weapons and wounds end to end over the in-memory transport, with the base mod's own weapons, items, Wound kinds and Treatments:
/// a player holds, loads and uses a weapon, zombies are wounded, the player is wounded, bleeds, bandages, and the HUD model reads
/// what the Server replicated.
/// </summary>
public sealed class WeaponCombatTests
{
    private static readonly ItemId Pistol = new("base:item/pistol_9mm");
    private static readonly ItemId Rounds = new("base:item/pistol_rounds_9mm");
    private static readonly ItemId Bat = new("base:item/baseball_bat");
    private static readonly ItemId Bandage = new("base:item/bandage");
    private static readonly ItemId Beans = new("base:item/canned_beans");

    private const string BandageTreatment = "base:treatment/bandage";

    // The skeleton faces -Z and a player at yaw 0 looks along -Z from 1.62 above their feet.
    private static readonly Vector3 PlayerAt = new(8, 80, 8);

    private sealed class Rig
    {
        public Rig(bool withWeapons = true)
        {
            var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RigTestData.RepoRoot(), "mods")));
            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Errors));
            var registry = loaded.Registry;
            Identity = GameIdentity.From(loaded, WorldGenerator.GeneratorVersion);
            var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
            var kinds = new WoundKindCatalog(registry.OfKind("wound_kind").Select(d => WoundKindJson.Parse(d.Json)));
            var options = new ServerOptions(Identity, WorldSeed: 3)
            {
                Items = items,
                SpawnPoint = PlayerAt,
                PlayerBody = new BodyConfig { WoundKinds = kinds },
            };
            if (withWeapons)
            {
                options = options with
                {
                    Weapons = new WeaponCatalog(
                        registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)),
                        registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)),
                        registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json))),
                    Treatments = new TreatmentCatalog(registry.OfKind("treatment").Select(d => TreatmentJson.Parse(d.Json)), kinds),
                };
            }

            Server = new GameServer(Network.CreateServer(), options);
            var traits = new TraitRegistry();
            BaseTraits.Register(traits);
            Zombies = new ZombieSystem(Server.World, ZombieContentLoader.Load(registry), traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
            Server.Zombies = Zombies;
            Client = new GameClient(Network.Connect(), Identity, "alice");
            Run(10);
            Assert.True(Server.TryGetPlayer(new ConnectionId(1), out var session));
            Session = session;
        }

        public GameIdentity Identity { get; }

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public ZombieSystem Zombies { get; }

        public GameClient Client { get; }

        public PlayerSession Session { get; }

        public long Tick { get; private set; }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(Tick);
                Zombies.Tick(Tick);
                Tick++;
                Client.Poll();
            }
        }

        public void Carry(ItemId item, int count = 1, ItemState? state = null) => Assert.True(Session.Carried.TryAdd(item, count, state).IsSuccess);

        /// <summary>Holds a weapon the player carries, as the client would ask, and lets the Server answer.</summary>
        public void Hold(ItemId weapon)
        {
            Client.Send(new HoldWeapon(weapon));
            Run(2);
            Assert.Equal(weapon, Session.Held);
        }

        /// <summary>Uses the Held weapon, waits for the answer, and returns the rejection if there was one.</summary>
        public CommandRejected? Use()
        {
            var before = Client.RejectionCount;
            var sequence = Client.Send(new UseWeapon());
            Run(2);
            return Client.RejectionCount > before && Client.LastRejection!.Sequence == sequence ? Client.LastRejection : null;
        }

        public uint SpawnZombie(Vector3 at)
        {
            Assert.True(Zombies.TrySpawn(new ZombieSpec(5, "base:zombie/walker", 1), at, 0f, out var id, out var problem), problem);
            return id;
        }

        public Body BodyOf(uint zombie)
        {
            Assert.True(Zombies.TryGetBody(zombie, out var body));
            return body;
        }
    }

    private static ItemState Loaded(int rounds, int condition = 100) =>
        ItemState.Create([new(WeaponService.RoundsValue, rounds), new(WeaponService.ConditionValue, condition)]);

    private static Vector3 AheadOfPlayer(float blocks) => PlayerAt + new Vector3(0, 0.3f, -blocks);

    [Fact]
    public void AFirearm_ShootsFarAndSpendsARound_AndAMeleeWeaponSpendsNoneAndStrikesOnlyNear()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(5));
        rig.Carry(Bat);
        var far = rig.SpawnZombie(AheadOfPlayer(15));

        rig.Hold(Bat);
        Assert.Null(rig.Use());
        Assert.Empty(rig.BodyOf(far).Wounds);
        Assert.Equal(0, WeaponService.RoundsOf(rig.Session.HeldState));

        rig.Run(60);
        rig.Hold(Pistol);
        Assert.Null(rig.Use());

        Assert.NotEmpty(rig.BodyOf(far).Wounds);
        Assert.Equal(4, WeaponService.RoundsOf(rig.Session.HeldState));
    }

    [Fact]
    public void AMeleeWeapon_WoundsAZombieWithinReach_WithoutSpendingAmmo()
    {
        var rig = new Rig();
        rig.Carry(Bat);
        var near = rig.SpawnZombie(AheadOfPlayer(0.7f));

        rig.Hold(Bat);
        Assert.Null(rig.Use());

        Assert.NotEmpty(rig.BodyOf(near).Wounds);
        Assert.Equal(0, WeaponService.RoundsOf(rig.Session.HeldState));
    }

    [Fact]
    public void AHit_CreatesWoundsOnTheServersZombieBody_AndOnlyTheServerDecidesTheDamage()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(3));
        var zombie = rig.SpawnZombie(AheadOfPlayer(8));
        var healthBefore = BodyPart_Total(rig.BodyOf(zombie));

        rig.Hold(Pistol);
        Assert.Null(rig.Use());

        var body = rig.BodyOf(zombie);
        var wound = Assert.Single(body.Wounds);
        Assert.Equal(DamageType.Pierce, wound.Type);
        Assert.True(wound.IsBleeding);
        Assert.True(BodyPart_Total(body) < healthBefore);
    }

    private static double BodyPart_Total(Body body) => Enum.GetValues<BodyPart>().Sum(body.Health);

    [Fact]
    public void ConditionLossPerUse_ComesFromTheWeaponDefinition_AndAMissedShotStillCostsARoundAndWear()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(5, 80));
        rig.Carry(Bat, 1, ItemState.Create([new(WeaponService.ConditionValue, 50)]));

        rig.Hold(Bat);
        Assert.Null(rig.Use());
        Assert.Equal(48, WeaponService.ConditionOf(rig.Session.HeldState)); // base:weapon/baseball_bat wearPerUse 2

        rig.Run(60);
        rig.Hold(Pistol);
        Assert.Null(rig.Use());
        Assert.Equal(79, WeaponService.ConditionOf(rig.Session.HeldState)); // base:weapon/pistol_9mm wearPerUse 1
        Assert.Equal(4, WeaponService.RoundsOf(rig.Session.HeldState)); // nothing in front of the player, still a shot
        Assert.Equal(1, rig.Session.Carried.CountOf(Pistol, rig.Session.HeldState));
    }

    [Fact]
    public void AWornWeapon_DealsLessDamage_ThanTheSameWeaponInFullCondition()
    {
        var fresh = new Rig();
        fresh.Carry(Pistol, 1, Loaded(3, 100));
        var worn = new Rig();
        worn.Carry(Pistol, 1, Loaded(3, 10));
        var a = fresh.SpawnZombie(AheadOfPlayer(8));
        var b = worn.SpawnZombie(AheadOfPlayer(8));

        fresh.Hold(Pistol);
        Assert.Null(fresh.Use());
        worn.Hold(Pistol);
        Assert.Null(worn.Use());

        Assert.True(BodyPart_Total(worn.BodyOf(b)) > BodyPart_Total(fresh.BodyOf(a)));
    }

    [Fact]
    public void ABrokenWeapon_IsRefused_AndNothingChanges()
    {
        var rig = new Rig();
        rig.Carry(Bat, 1, ItemState.Create([new(WeaponService.ConditionValue, 2)]));
        var near = rig.SpawnZombie(AheadOfPlayer(0.7f));
        rig.Hold(Bat);

        Assert.Null(rig.Use());
        Assert.Equal(0, WeaponService.ConditionOf(rig.Session.HeldState));
        var wounds = rig.BodyOf(near).Wounds.Count;

        rig.Run(60);
        var rejected = rig.Use();

        Assert.NotNull(rejected);
        Assert.Equal(CommandRejection.Invalid, rejected.Reason);
        Assert.Contains("broken", rejected.Detail, StringComparison.Ordinal);
        Assert.Equal(wounds, rig.BodyOf(near).Wounds.Count);
        Assert.Equal(0, WeaponService.ConditionOf(rig.Session.HeldState));
    }

    [Fact]
    public void AFirearmWithNoRounds_IsRefused_UntilTheAmmoItemIsLoaded()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(0));
        rig.Carry(Rounds, 12);
        rig.Hold(Pistol);

        var rejected = rig.Use();
        Assert.NotNull(rejected);
        Assert.Contains("ammo", rejected.Detail, StringComparison.Ordinal);

        rig.Client.Send(new LoadWeapon());
        rig.Run(2);

        Assert.Equal(12, WeaponService.RoundsOf(rig.Session.HeldState));
        Assert.Equal(0, rig.Session.Carried.CountOf(Rounds));
        Assert.Equal(1, rig.Session.Carried.CountOf(Pistol, rig.Session.HeldState));
        Assert.Null(rig.Use());
        Assert.Equal(11, WeaponService.RoundsOf(rig.Session.HeldState));
    }

    [Fact]
    public void TheRateOfFire_IsEnforcedByTheServer()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(5));
        rig.Hold(Pistol);

        Assert.Null(rig.Use());
        var tooSoon = rig.Use();

        Assert.NotNull(tooSoon);
        Assert.Equal(4, WeaponService.RoundsOf(rig.Session.HeldState));
    }

    [Fact]
    public void TheServerRefusesWhatItCannotAllow()
    {
        var rig = new Rig();
        rig.Carry(Beans);
        rig.Carry(Bat);

        void Expect(Func<CommandRejected?> act)
        {
            var rejected = act();
            Assert.NotNull(rejected);
            Assert.Equal(CommandRejection.Invalid, rejected.Reason);
        }

        CommandRejected? Send<T>(T command)
            where T : INetCommand<T>
        {
            var before = rig.Client.RejectionCount;
            rig.Client.Send(command);
            rig.Run(2);
            return rig.Client.RejectionCount > before ? rig.Client.LastRejection : null;
        }

        Expect(() => Send(new UseWeapon()));
        Expect(() => Send(new LoadWeapon()));
        Expect(() => Send(new HoldWeapon(Beans)));
        Expect(() => Send(new HoldWeapon(Pistol)));
        Expect(() => Send(new TreatWounds(BodyPart.Torso, BandageTreatment)));
        Expect(() => Send(new TreatWounds(BodyPart.Torso, "base:treatment/nothing")));
        Assert.Null(rig.Session.Held);
    }

    [Fact]
    public void AServerWithNoWeaponContent_RefusesEveryWeaponCommand()
    {
        var rig = new Rig(withWeapons: false);
        rig.Carry(Bat);

        rig.Client.Send(new HoldWeapon(Bat));
        rig.Run(2);

        Assert.Equal(1, rig.Client.RejectionCount);
        Assert.Null(rig.Session.Held);
    }

    [Fact]
    public void ADeadPlayer_CannotUseAWeapon_AndHoldsNothingAfterRespawning()
    {
        var rig = new Rig();
        rig.Carry(Bat);
        rig.Hold(Bat);

        rig.Server.Damage(rig.Session, BodyPart.Torso, DamageType.Cut, 1000);
        rig.Run(2);
        Assert.Null(rig.Session.Held);

        var rejected = rig.Use();
        Assert.NotNull(rejected);
        Assert.Equal(CommandRejection.Dead, rejected.Reason);
    }

    [Fact]
    public void APlayersWound_Bleeds_AndTheBloodLossReplicatesToTheirClient()
    {
        var rig = new Rig();
        rig.Run(5);
        var full = rig.Client.Status.Body.BloodVolume;

        rig.Server.Damage(rig.Session, BodyPart.Torso, DamageType.Cut, 20);
        rig.Run(90);

        Assert.True(rig.Session.Body.BloodVolume < full);
        var seen = rig.Client.Status.Body;
        Assert.NotEmpty(seen.Wounds);
        Assert.True(seen.TotalBleedRate.MillilitersPerMinute > 0);
        Assert.True(seen.BloodVolume < full);
        Assert.True(seen.Health(BodyPart.Torso) < 100);
    }

    [Fact]
    public void ABandage_StopsTheBleeding_ConsumesTheBandage_AndTheClientSeesBoth()
    {
        var rig = new Rig();
        rig.Carry(Bandage, 2);
        rig.Server.Damage(rig.Session, BodyPart.LeftArm, DamageType.Cut, 20);
        rig.Run(40);
        Assert.True(rig.Client.Status.Body.TotalBleedRate.MillilitersPerMinute > 0);

        rig.Client.Send(new TreatWounds(BodyPart.LeftArm, BandageTreatment));
        rig.Run(40);

        Assert.Equal(0, rig.Client.RejectionCount);
        Assert.Equal(1, rig.Session.Carried.CountOf(Bandage));
        Assert.Equal(0, rig.Session.Body.TotalBleedRate.MillilitersPerMinute);
        Assert.Equal(0, rig.Client.Status.Body.TotalBleedRate.MillilitersPerMinute);
        Assert.All(rig.Client.Status.Body.Wounds.Where(w => w.Part == BodyPart.LeftArm), w => Assert.True(w.IsBandaged));
    }

    [Fact]
    public void ABandage_IsRefusedWhenThereIsNothingToTreatOrNoBandageIsCarried()
    {
        var rig = new Rig();
        rig.Carry(Bandage);

        rig.Client.Send(new TreatWounds(BodyPart.Head, BandageTreatment));
        rig.Run(2);
        Assert.Equal(1, rig.Client.RejectionCount);
        Assert.Equal(1, rig.Session.Carried.CountOf(Bandage));

        rig.Server.Damage(rig.Session, BodyPart.Head, DamageType.Cut, 20);
        Assert.True(rig.Session.Carried.Stacks.Count > 0);
        rig.Client.Send(new TreatWounds(BodyPart.Head, BandageTreatment));
        rig.Run(2);
        Assert.Equal(1, rig.Client.RejectionCount);
        Assert.Equal(0, rig.Session.Carried.CountOf(Bandage));

        rig.Server.Damage(rig.Session, BodyPart.Torso, DamageType.Cut, 20);
        rig.Client.Send(new TreatWounds(BodyPart.Torso, BandageTreatment));
        rig.Run(2);
        Assert.Equal(2, rig.Client.RejectionCount);
    }

    [Fact]
    public void AmmoAndCondition_ReplicateToTheHud_AndTheBandageOfferFollowsWhatIsCarried()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(9, 72));
        rig.Hold(Pistol);
        rig.Run(5);

        var hud = Hud(rig);
        Assert.Equal(9, hud.Ammo);
        Assert.Equal(0.72, hud.ConditionFraction, 3);

        Assert.Null(rig.Use());
        rig.Run(5);
        hud = Hud(rig);
        Assert.Equal(8, hud.Ammo);
        Assert.Equal(0.71, hud.ConditionFraction, 3);

        rig.Server.Damage(rig.Session, BodyPart.RightLeg, DamageType.Cut, 20);
        rig.Run(5);
        hud = Hud(rig);
        Assert.True(hud.IsBleeding);
        Assert.Equal(PaletteRole.Danger, hud.BleedRole);
    }

    private static HudModel Hud(Rig rig)
    {
        var status = rig.Client.Status;
        return new HudModel(status.Body, new Needs(), status.Weapon, status.WeaponState, new Localizer([]));
    }

    [Fact]
    public void AMeleeWeaponHeld_ShowsNoAmmoOnTheHud_AndNothingHeldShowsNone()
    {
        var rig = new Rig();
        rig.Carry(Bat);
        rig.Run(5);
        Assert.Null(rig.Client.Status.Weapon);

        rig.Hold(Bat);
        rig.Run(5);

        Assert.Equal(0, Hud(rig).Ammo);
        Assert.Equal(1.0, Hud(rig).ConditionFraction, 3);
    }

    [Fact]
    public void ATreatmentTheHudOffers_CanBeSentAsThePlayersOwnCommand()
    {
        var rig = new Rig();
        rig.Carry(Bandage);
        rig.Server.Damage(rig.Session, BodyPart.LeftLeg, DamageType.Cut, 20);
        rig.Run(5);

        var treatments = new TreatmentCatalog(
            [TreatmentJson.Parse("""{ "id": "base:treatment/bandage", "stopsBleeding": true, "time": 5, "consumes": "base:item/bandage" }""")],
            new WoundKindCatalog([]));
        var status = rig.Client.Status;
        var hud = new HudModel(status.Body, new Needs(), null, null, new Localizer([]), treatments: treatments, holds: item => rig.Session.Carried.CountOf(item) > 0);
        var offer = Assert.Single(hud.Parts.Single(p => p.Part == BodyPart.LeftLeg).Treatments);
        Assert.True(offer.HasItem);

        rig.Client.Send(new TreatWounds(BodyPart.LeftLeg, offer.Id));
        rig.Run(5);

        Assert.Equal(0, rig.Client.RejectionCount);
        Assert.Equal(0, rig.Client.Status.Body.TotalBleedRate.MillilitersPerMinute);
    }

    [Fact]
    public void KillingAZombieWithAWeapon_CreditsTheKillToThePlayersMemorial()
    {
        var rig = new Rig();
        rig.Carry(Pistol, 1, Loaded(30));
        var zombie = rig.SpawnZombie(AheadOfPlayer(6));
        rig.Hold(Pistol);

        for (var shot = 0; shot < 30 && rig.BodyOf(zombie).IsAlive; shot++)
        {
            rig.Run(20);
            Assert.Null(rig.Use());
        }

        Assert.False(rig.BodyOf(zombie).IsAlive);
        Assert.Equal(1, rig.Session.Kills);
    }
}
