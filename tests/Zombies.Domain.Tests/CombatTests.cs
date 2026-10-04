using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class CombatTests
{
    private static readonly BodyId Id = new(1);

    [Fact]
    public void TakeHit_CreatesBleedingWoundAndReducesPartHealth()
    {
        var body = new Body(Id);

        var result = body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Events, e => e is DamageTaken { Part: BodyPart.Torso, Damage: 20 });
        Assert.Contains(result.Events, e => e is WoundCreated { Part: BodyPart.Torso, Type: DamageType.Cut });
        var wound = Assert.Single(body.Wounds);
        Assert.True(wound.IsBleeding);
        Assert.Equal(80, wound.BleedRate.MillilitersPerMinute, 6);
        Assert.Equal(80, body.Health(BodyPart.Torso), 6);
    }

    [Fact]
    public void TakeHit_ProtectionReducesEffectiveDamage()
    {
        var body = new Body(Id);

        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20, protection: 0.5);

        Assert.Equal(90, body.Health(BodyPart.Torso), 6);
        Assert.Equal(40, Assert.Single(body.Wounds).BleedRate.MillilitersPerMinute, 6);
    }

    [Fact]
    public void TakeHit_FullyAbsorbed_CreatesNoWound()
    {
        var body = new Body(Id);

        var result = body.TakeHit(BodyPart.Head, DamageType.Blunt, 30, protection: 1);

        Assert.IsType<DamageAbsorbed>(Assert.Single(result.Events));
        Assert.Empty(body.Wounds);
        Assert.Equal(100, body.Health(BodyPart.Head));
    }

    [Theory]
    [InlineData(0, 0, CombatError.InvalidDamage)]
    [InlineData(-5, 0, CombatError.InvalidDamage)]
    [InlineData(10, -0.1, CombatError.InvalidProtection)]
    [InlineData(10, 1.1, CombatError.InvalidProtection)]
    public void TakeHit_RejectsInvalidArguments(double damage, double protection, CombatError expected)
    {
        var body = new Body(Id);

        var result = body.TakeHit(BodyPart.Torso, DamageType.Cut, damage, protection);

        Assert.Equal(expected, result.Error);
        Assert.Empty(body.Wounds);
    }

    [Fact]
    public void Advance_BleedingDrainsBlood()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);

        var result = body.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(4.2, body.BloodVolume.Liters, 6);
        var lost = Assert.IsType<BloodLost>(Assert.Single(result.Events));
        Assert.Equal(0.8, lost.Amount.Liters, 6);
        Assert.True(body.IsAlive);
    }

    [Fact]
    public void Bandage_StopsBleeding()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.LeftArm, DamageType.Pierce, 10);

        var result = body.Bandage(BodyPart.LeftArm);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Assert.IsType<WoundsBandaged>(Assert.Single(result.Events)).Count);
        Assert.Equal(VolumeFlow.Zero, body.TotalBleedRate);
        Assert.Empty(body.Advance(TimeSpan.FromMinutes(30)).Events);
        Assert.Equal(5, body.BloodVolume.Liters, 6);
    }

    [Fact]
    public void Bandage_OnlyAffectsTheChosenPart()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 10);
        body.TakeHit(BodyPart.RightArm, DamageType.Cut, 10);

        body.Bandage(BodyPart.LeftArm);

        Assert.Equal(40, body.TotalBleedRate.MillilitersPerMinute, 6);
    }

    [Fact]
    public void Bandage_WithNothingBleeding_Fails()
    {
        var body = new Body(Id);

        Assert.Equal(CombatError.NothingToBandage, body.Bandage(BodyPart.Torso).Error);

        body.TakeHit(BodyPart.Torso, DamageType.Cut, 5);
        body.Bandage(BodyPart.Torso);

        Assert.Equal(CombatError.NothingToBandage, body.Bandage(BodyPart.Torso).Error);
    }

    [Fact]
    public void TakeHit_ThatDestroysALimb_DismembersItAndLeavesAStump()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 30);

        var result = body.TakeHit(BodyPart.LeftArm, DamageType.Cut, 100);

        Assert.Contains(result.Events, e => e is BodyPartLost { Part: BodyPart.LeftArm });
        Assert.True(body.IsMissing(BodyPart.LeftArm));
        Assert.Equal([BodyPart.LeftArm], body.MissingParts);
        var stump = Assert.Single(body.Wounds);
        Assert.True(stump.IsStump);
        Assert.Equal(30, stump.BleedRate.MillilitersPerMinute, 6);
        Assert.True(body.IsAlive);
    }

    [Fact]
    public void TakeHit_OnAMissingPart_Fails()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.RightLeg, DamageType.Bite, 100);

        Assert.Equal(CombatError.PartMissing, body.TakeHit(BodyPart.RightLeg, DamageType.Bite, 5).Error);
    }

    [Fact]
    public void Stump_BleedsOutAndBandageStopsIt()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.LeftLeg, DamageType.Cut, 100);

        Assert.True(body.Bandage(BodyPart.LeftLeg).IsSuccess);
        body.Advance(TimeSpan.FromHours(3));

        Assert.True(body.IsAlive);
        Assert.Equal(5, body.BloodVolume.Liters, 6);
    }

    [Fact]
    public void Advance_BleedingForLongEnough_KillsByBloodLoss()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.RightLeg, DamageType.Cut, 100);

        var result = body.Advance(TimeSpan.FromMinutes(120));

        Assert.False(body.IsAlive);
        Assert.Contains(result.Events, e => e is BodyDied { Cause: DeathCause.BloodLoss });
    }

    [Fact]
    public void TakeHit_DestroyingTheTorsoOrHead_KillsByTrauma()
    {
        var torso = new Body(Id);
        var head = new Body(new BodyId(2));

        var torsoResult = torso.TakeHit(BodyPart.Torso, DamageType.Blunt, 100);
        var headResult = head.TakeHit(BodyPart.Head, DamageType.Cut, 100);

        Assert.Contains(torsoResult.Events, e => e is BodyDied { Cause: DeathCause.Trauma });
        Assert.Contains(headResult.Events, e => e is BodyPartLost { Part: BodyPart.Head });
        Assert.Contains(headResult.Events, e => e is BodyDied { Cause: DeathCause.Trauma });
        Assert.False(torso.IsAlive);
        Assert.False(head.IsAlive);
    }

    [Fact]
    public void ADeadBody_TakesNoMoreHitsOrBandages_AndAdvanceIsQuiet()
    {
        var body = new Body(Id);
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 100);

        Assert.Equal(CombatError.AlreadyDead, body.TakeHit(BodyPart.Head, DamageType.Cut, 5).Error);
        Assert.Equal(CombatError.AlreadyDead, body.Bandage(BodyPart.Torso).Error);
        Assert.Empty(body.Advance(TimeSpan.FromMinutes(5)).Events);
    }

    [Fact]
    public void Advance_RejectsNonPositiveDurations()
    {
        var body = new Body(Id);

        Assert.Equal(CombatError.InvalidDuration, body.Advance(TimeSpan.Zero).Error);
        Assert.Equal(CombatError.InvalidDuration, body.Advance(TimeSpan.FromSeconds(-1)).Error);
    }

    [Fact]
    public void SpawningWithMissingParts_StartsWithThemMissing()
    {
        var body = new Body(Id, missingAtSpawn: [BodyPart.LeftLeg, BodyPart.RightArm]);

        Assert.True(body.IsAlive);
        Assert.Empty(body.Wounds);
        Assert.Equal(0, body.Health(BodyPart.LeftLeg));
        Assert.Equal([BodyPart.RightArm, BodyPart.LeftLeg], body.MissingParts);
        Assert.Equal(CombatError.PartMissing, body.TakeHit(BodyPart.LeftLeg, DamageType.Cut, 5).Error);
    }

    [Fact]
    public void SpawningWithoutATorso_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Body(Id, missingAtSpawn: [BodyPart.Torso]));
    }
}
