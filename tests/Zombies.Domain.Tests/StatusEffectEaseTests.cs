using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;

namespace Zombies.Domain.Tests;

public sealed class StatusEffectEaseTests
{
    private const string Withdrawal = "test:status_effect/withdrawal";
    private const string Plain = "test:status_effect/plain";

    private static readonly CreatureId Bob = new(1);
    private static readonly StatName MoveSpeed = new("move_speed");

    private static readonly StatusEffectCatalog Catalog = new(
        new[]
        {
            """
            { "id": "test:status_effect/withdrawal", "category": "ailment",
              "periodic": [ { "change": "ache", "every": 50, "amount": 1 } ],
              "stages": [
                { "name": "mild", "after": 0, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.9 } ] },
                { "name": "bad", "after": 100, "modifiers": [ { "stat": "move_speed", "operation": "multiply", "value": 0.7 } ],
                  "periodic": [ { "change": "shakes", "every": 10, "amount": 2 } ] }
              ] }
            """,
            """{ "id": "test:status_effect/plain", "category": "buff", "duration": 100 }""",
        }.Select(StatusEffectJson.Parse));

    private readonly CreatureEffects _effects = new(Bob, Catalog);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Ease_OfAnEffectThatIsNotActive_IsAnError()
    {
        Assert.Equal(EffectError.NotActive, _effects.Ease(Withdrawal).Error);
    }

    [Fact]
    public void Ease_TakesTheEffectBackOneStage_AndItsModifiersFollow()
    {
        _effects.Apply(Withdrawal);
        _effects.Advance(S(150));
        Assert.Equal(0.7, _effects.EffectiveValue(MoveSpeed, 1), 9);

        var result = _effects.Ease(Withdrawal);

        Assert.Equal([new EffectStageChanged(Bob, Withdrawal, "mild"), new EffectEased(Bob, Withdrawal, "mild")], result.Events);
        Assert.Equal(0.9, _effects.EffectiveValue(MoveSpeed, 1), 9);
        Assert.Equal("mild", Assert.Single(_effects.Effects).Stage);
    }

    [Fact]
    public void AnEasedEffect_ProgressesAgainFromTheEasedStage()
    {
        _effects.Apply(Withdrawal);
        _effects.Advance(S(150));
        _effects.Ease(Withdrawal);

        _effects.Advance(S(99));
        Assert.Equal("mild", Assert.Single(_effects.Effects).Stage);

        var result = _effects.Advance(S(1));

        Assert.Contains(new EffectStageChanged(Bob, Withdrawal, "bad"), result.Events);
    }

    [Fact]
    public void Ease_KeepsThePeriodicChangesOfTheEffectOnTheirSchedule()
    {
        _effects.Apply(Withdrawal);
        _effects.Advance(S(120));
        _effects.Ease(Withdrawal);

        var result = _effects.Advance(S(30));

        Assert.Equal([new EffectPeriodicChange(Bob, Withdrawal, new StatName("ache"), 1)], result.Events.OfType<EffectPeriodicChange>());
    }

    [Fact]
    public void Ease_InTheFirstStage_EndsTheEffect()
    {
        _effects.Apply(Withdrawal);

        var result = _effects.Ease(Withdrawal);

        Assert.Equal(new EffectEased(Bob, Withdrawal, null), Assert.Single(result.Events));
        Assert.Empty(_effects.Effects);
        Assert.Empty(_effects.Modifiers);
    }

    [Fact]
    public void Ease_OfAnEffectWithoutStages_EndsIt()
    {
        _effects.Apply(Plain);

        _effects.Ease(Plain);

        Assert.False(_effects.Has(Plain));
    }
}
