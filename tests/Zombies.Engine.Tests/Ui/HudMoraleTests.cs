using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class HudMoraleTests
{
    private static readonly MoraleCatalog Catalog = new(
        new[]
        {
            """{ "id": "test:morale_source/teammate_death", "trigger": { "event": "teammate_died" }, "size": -30, "duration": 1000 }""",
            """{ "id": "test:morale_source/hot_meal", "trigger": { "event": "ate_hot_meal" }, "size": 4, "duration": 1000 }""",
        }.Select(MoraleSourceJson.Parse),
        new[]
        {
            """{ "id": "test:morale_band/low", "min": -100 }""",
            """{ "id": "test:morale_band/steady", "min": -25 }""",
        }.Select(MoraleBandJson.Parse));

    private static Localizer English()
    {
        Assert.True(StringTable.TryParse("""
            { "language": "en", "strings": {
              "hud.morale": "Morale",
              "morale_band.test.morale_band.low": "Low spirits",
              "morale_band.test.morale_band.steady": "Steady",
              "morale_source.test.morale_source.teammate_death": "A teammate died",
              "morale_source.test.morale_source.hot_meal": "A hot meal" } }
            """, out var table, out var error), error);
        return new Localizer([table]);
    }

    private static HudModel Model(Morale? morale) =>
        new(new Body(new BodyId(1)), new Needs(), null, null, English(), morale: morale);

    [Fact]
    public void WithoutMorale_ThereIsNothingToShow()
    {
        var model = Model(null);

        Assert.Null(model.MoraleLabel);
        Assert.Empty(model.MoraleSources);
        Assert.Null(model.MoraleTooltip);
    }

    [Fact]
    public void TheHud_ShowsTheMoraleBand_AndItsColorFollowsWhetherSpiritsAreLow()
    {
        var morale = new Morale(Catalog);
        var model = Model(morale);

        Assert.Equal("Steady", model.MoraleLabel);
        Assert.Equal(PaletteRole.Good, model.MoraleRole);

        morale.OnEvent(new StatName("teammate_died"));

        Assert.Equal("Low spirits", model.MoraleLabel);
        Assert.Equal(PaletteRole.Warning, model.MoraleRole);
    }

    [Fact]
    public void TheTooltip_ListsTheSourcesBehindMorale_BiggestFirst()
    {
        var morale = new Morale(Catalog);
        morale.OnEvent(new StatName("ate_hot_meal"));
        morale.OnEvent(new StatName("teammate_died"));
        var model = Model(morale);

        Assert.Equal([new MoraleSourceStatus("A teammate died", -30), new MoraleSourceStatus("A hot meal", 4)], model.MoraleSources);
        var tooltip = model.MoraleTooltip!;
        Assert.Equal("Morale", tooltip.Title);
        Assert.Equal("Low spirits", tooltip.Caption);
        Assert.Contains("A teammate died -30", string.Join(' ', tooltip.Lines));
        Assert.Contains("A hot meal +4", string.Join(' ', tooltip.Lines));
    }
}
