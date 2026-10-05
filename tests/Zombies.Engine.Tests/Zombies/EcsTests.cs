using Zombies.Engine.Ecs;

namespace Zombies.Engine.Tests;

public sealed class EcsTests
{
    private readonly record struct Health(int Value);

    private readonly record struct Speed(double Value);

    [Fact]
    public void AnEntity_HoldsComponentsOfAnyTypeAndGivesThemBack()
    {
        var world = new EcsWorld();
        var entity = world.Create();

        world.Set(entity, new Health(10));
        world.Set(entity, new Speed(1.5));

        Assert.True(world.Has<Health>(entity));
        Assert.True(world.TryGet<Speed>(entity, out var speed));
        Assert.Equal(1.5, speed.Value);
        Assert.Equal(1, world.Count);
    }

    [Fact]
    public void SettingAComponentAgain_ReplacesIt()
    {
        var world = new EcsWorld();
        var entity = world.Create();
        world.Set(entity, new Health(10));

        world.Set(entity, new Health(3));

        Assert.True(world.TryGet<Health>(entity, out var health));
        Assert.Equal(3, health.Value);
        Assert.Single(world.Query<Health>());
    }

    [Fact]
    public void AMissingComponent_IsReportedNotThrown()
    {
        var world = new EcsWorld();
        var entity = world.Create();

        Assert.False(world.Has<Health>(entity));
        Assert.False(world.TryGet<Health>(entity, out _));
        Assert.False(world.Remove<Health>(entity));
        Assert.Empty(world.Query<Health>());
    }

    [Fact]
    public void DestroyingAnEntity_RemovesItsComponents_AndLeavesOthersAlone()
    {
        var world = new EcsWorld();
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();
        world.Set(a, new Health(1));
        world.Set(b, new Health(2));
        world.Set(c, new Health(3));

        Assert.True(world.Destroy(a));

        Assert.False(world.IsAlive(a));
        Assert.Equal([(b, new Health(2)), (c, new Health(3))], world.Query<Health>().OrderBy(q => q.Component.Value));
        Assert.Equal(2, world.Count);
    }

    [Fact]
    public void AStaleHandle_IsNeverMistakenForTheEntityThatReusedItsSlot()
    {
        var world = new EcsWorld();
        var first = world.Create();
        world.Set(first, new Health(1));
        world.Destroy(first);

        var second = world.Create();

        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first, second);
        Assert.False(world.IsAlive(first));
        Assert.True(world.IsAlive(second));
        Assert.False(world.Has<Health>(first));
        Assert.False(world.Has<Health>(second));
        Assert.False(world.Destroy(first));
        Assert.Throws<ArgumentException>(() => world.Set(first, new Health(9)));
    }

    [Fact]
    public void ANoneHandle_IsNotAlive()
    {
        var world = new EcsWorld();

        Assert.True(Entity.None.IsNone);
        Assert.False(world.IsAlive(Entity.None));
        Assert.False(world.Destroy(Entity.None));
    }

    [Fact]
    public void Query_ListsOnlyEntitiesWithThatComponent()
    {
        var world = new EcsWorld();
        var withBoth = world.Create();
        var healthOnly = world.Create();
        world.Set(withBoth, new Health(1));
        world.Set(withBoth, new Speed(2));
        world.Set(healthOnly, new Health(3));

        Assert.Equal(2, world.Query<Health>().Count());
        Assert.Equal([withBoth], world.Query<Speed>().Select(q => q.Entity));
    }

    [Fact]
    public void RemovingOneComponent_KeepsTheRest()
    {
        var world = new EcsWorld();
        var entity = world.Create();
        world.Set(entity, new Health(1));
        world.Set(entity, new Speed(2));

        Assert.True(world.Remove<Health>(entity));

        Assert.False(world.Has<Health>(entity));
        Assert.True(world.Has<Speed>(entity));
    }
}
