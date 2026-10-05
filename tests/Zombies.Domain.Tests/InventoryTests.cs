using UnitsNet;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class InventoryTests
{
    private static readonly ItemId Beans = new("base:item/canned_beans");
    private static readonly ItemId Water = new("base:item/water_bottle");
    private static readonly ItemId Rifle = new("base:item/rifle");
    private static readonly ItemId Scope = new("base:item/scope");
    private static readonly ContainerId Backpack = new(1);
    private static readonly ContainerId Crate = new(2);
    private static readonly ContainerId Pouch = new(3);

    private readonly InventoryService _service;
    private readonly InMemoryContainerRepository _containers = new();

    public InventoryTests()
    {
        var catalog = new ItemCatalog(
        [
            new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
            new ItemDefinition(Water, Mass.FromKilograms(1.0), Volume.FromLiters(1.0), maxStack: 2),
            new ItemDefinition(Rifle, Mass.FromKilograms(3.0), Volume.FromLiters(4.0), maxStack: 5),
            new ItemDefinition(Scope, Mass.FromKilograms(0.5), Volume.FromLiters(0.5), maxStack: 1),
        ]);
        _service = new InventoryService(catalog, _containers);
        Assert.True(_service.AddContainer(new Container(Backpack, Mass.FromKilograms(10), Volume.FromLiters(10), catalog)).IsSuccess);
        Assert.True(_service.AddContainer(new Container(Crate, Mass.FromKilograms(2), Volume.FromLiters(100), catalog)).IsSuccess);
        Assert.True(_service.AddContainer(new Container(Pouch, Mass.FromKilograms(100), Volume.FromLiters(1), catalog)).IsSuccess);
    }

    private Container Get(ContainerId id) => _containers.TryGet(id, out var c) ? c : throw new InvalidOperationException();

    [Fact]
    public void AddItems_FillsPartialStacksThenCreatesNewOnes()
    {
        var result = _service.AddItems(Backpack, Beans, 6);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ItemsAdded(Backpack, Beans, 6), Assert.Single(result.Events));
        Assert.Equal([4, 2], Get(Backpack).Stacks.Select(s => s.Count));

        _service.AddItems(Backpack, Beans, 2);

        Assert.Equal([4, 4], Get(Backpack).Stacks.Select(s => s.Count));
    }

    [Fact]
    public void AddItems_OverMassLimit_FailsWithoutChangingState()
    {
        var result = _service.AddItems(Crate, Water, 3);

        Assert.Equal(InventoryError.ExceedsMassLimit, result.Error);
        Assert.Empty(result.Events);
        Assert.Empty(Get(Crate).Stacks);
    }

    [Fact]
    public void AddItems_OverVolumeLimit_Fails()
    {
        var result = _service.AddItems(Pouch, Water, 2);

        Assert.Equal(InventoryError.ExceedsVolumeLimit, result.Error);
        Assert.Empty(Get(Pouch).Stacks);
    }

    [Fact]
    public void Totals_UseUnitMassAndVolume()
    {
        _service.AddItems(Backpack, Beans, 5);
        _service.AddItems(Backpack, Water, 1);

        Assert.Equal(3.0, Get(Backpack).TotalMass.Kilograms, 9);
        Assert.Equal(2.75, Get(Backpack).TotalVolume.Liters, 9);
    }

    [Fact]
    public void RemoveItems_TakesFromStacksAndRejectsOverdraw()
    {
        _service.AddItems(Backpack, Beans, 6);

        Assert.True(_service.RemoveItems(Backpack, Beans, 3).IsSuccess);
        Assert.Equal(3, Get(Backpack).CountOf(Beans));
        Assert.Equal(InventoryError.InsufficientItems, _service.RemoveItems(Backpack, Beans, 4).Error);
        Assert.Equal(3, Get(Backpack).CountOf(Beans));
    }

    [Fact]
    public void MoveItems_MovesPartOfAStackBetweenContainers()
    {
        _service.AddItems(Backpack, Beans, 4);
        var stack = Get(Backpack).Stacks.Single();

        var result = _service.MoveItems(Backpack, stack.Id, Crate, 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ItemsMoved(Backpack, Crate, Beans, 3), Assert.Single(result.Events));
        Assert.Equal(1, Get(Backpack).CountOf(Beans));
        Assert.Equal(3, Get(Crate).CountOf(Beans));
    }

    [Fact]
    public void MoveItems_ThatDoesNotFitDestination_ChangesNothing()
    {
        _service.AddItems(Backpack, Water, 3);
        var stack = Get(Backpack).Stacks[0];

        Assert.True(_service.MoveItems(Backpack, stack.Id, Crate, 2).IsSuccess);

        var remaining = Get(Backpack).Stacks.Single();
        Assert.Equal(InventoryError.ExceedsMassLimit, _service.MoveItems(Backpack, remaining.Id, Crate, 1).Error);
        Assert.Equal(1, Get(Backpack).CountOf(Water));
        Assert.Equal(2, Get(Crate).CountOf(Water));
    }

    [Fact]
    public void MoveItems_RejectsBadArguments()
    {
        _service.AddItems(Backpack, Beans, 2);
        var stack = Get(Backpack).Stacks.Single();

        Assert.Equal(InventoryError.SameContainer, _service.MoveItems(Backpack, stack.Id, Backpack, 1).Error);
        Assert.Equal(InventoryError.InvalidCount, _service.MoveItems(Backpack, stack.Id, Crate, 0).Error);
        Assert.Equal(InventoryError.InsufficientItems, _service.MoveItems(Backpack, stack.Id, Crate, 3).Error);
        Assert.Equal(InventoryError.UnknownStack, _service.MoveItems(Backpack, new StackId(99), Crate, 1).Error);
        Assert.Equal(InventoryError.UnknownContainer, _service.MoveItems(Backpack, stack.Id, new ContainerId(99), 1).Error);
    }

    [Fact]
    public void SplitStack_CreatesNewStackAndKeepsTotals()
    {
        _service.AddItems(Backpack, Beans, 4);
        var stack = Get(Backpack).Stacks.Single();

        var result = _service.SplitStack(Backpack, stack.Id, 1);

        Assert.True(result.IsSuccess);
        var split = Assert.IsType<StackSplit>(Assert.Single(result.Events));
        Assert.Equal(stack.Id, split.Source);
        Assert.Equal([3, 1], Get(Backpack).Stacks.Select(s => s.Count));
        Assert.Equal(4, Get(Backpack).CountOf(Beans));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void SplitStack_RejectsCountsThatLeaveAnEmptyStack(int count)
    {
        _service.AddItems(Backpack, Beans, 4);
        var stack = Get(Backpack).Stacks.Single();

        Assert.Equal(InventoryError.InvalidCount, _service.SplitStack(Backpack, stack.Id, count).Error);
    }

    [Fact]
    public void MergeStacks_CombinesWithinMaxStack()
    {
        _service.AddItems(Backpack, Beans, 4);
        var first = Get(Backpack).Stacks.Single();
        var created = ((StackSplit)_service.SplitStack(Backpack, first.Id, 1).Events.Single()).Created;

        var result = _service.MergeStacks(Backpack, first.Id, created);

        Assert.True(result.IsSuccess);
        Assert.Equal(new StacksMerged(Backpack, first.Id, created, 4), Assert.Single(result.Events));
        Assert.Equal([4], Get(Backpack).Stacks.Select(s => s.Count));
    }

    [Fact]
    public void MergeStacks_RejectsFullDifferentAndIdenticalStacks()
    {
        _service.AddItems(Backpack, Beans, 8);
        _service.AddItems(Backpack, Water, 1);
        var stacks = Get(Backpack).Stacks;

        Assert.Equal(InventoryError.StackFull, _service.MergeStacks(Backpack, stacks[0].Id, stacks[1].Id).Error);
        Assert.Equal(InventoryError.ItemMismatch, _service.MergeStacks(Backpack, stacks[0].Id, stacks[2].Id).Error);
        Assert.Equal(InventoryError.SameStack, _service.MergeStacks(Backpack, stacks[0].Id, stacks[0].Id).Error);
    }

    [Fact]
    public void Commands_RejectUnknownItemsAndCounts()
    {
        Assert.Equal(InventoryError.UnknownItem, _service.AddItems(Backpack, new ItemId("base:item/missing"), 1).Error);
        Assert.Equal(InventoryError.InvalidCount, _service.AddItems(Backpack, Beans, 0).Error);
        Assert.Equal(InventoryError.UnknownContainer, _service.AddItems(new ContainerId(99), Beans, 1).Error);
        Assert.Equal(InventoryError.DuplicateContainer, _service.AddContainer(Get(Backpack)).Error);
    }

    [Fact]
    public void ItemId_RejectsMalformedContentIds()
    {
        Assert.Throws<ArgumentException>(() => new ItemId("no-namespace"));
        Assert.Throws<ArgumentException>(() => new ItemId("Base:Item"));
        Assert.False(ItemId.TryParse(null, out _));
        Assert.True(ItemId.TryParse("mymod:item/rusty_knife", out var id));
        Assert.Equal("mymod:item/rusty_knife", id.ToString());
    }

    private static ItemState Worn(int condition, params ItemId[] attached) =>
        ItemState.Create([new("condition", condition)], attached.Select(a => new KeyValuePair<ItemId, int>(a, 1)));

    [Fact]
    public void ItemState_ComparesByValue()
    {
        Assert.Equal(Worn(40, Scope), Worn(40, Scope));
        Assert.Equal(Worn(40, Scope).GetHashCode(), Worn(40, Scope).GetHashCode());
        Assert.NotEqual(Worn(40, Scope), Worn(41, Scope));
        Assert.NotEqual(Worn(40, Scope), Worn(40));
    }

    [Fact]
    public void AddItems_KeepsStateOnStackAndEvent()
    {
        var state = Worn(40, Scope);

        var result = _service.AddItems(Backpack, Rifle, 1, state);

        Assert.Equal(new ItemsAdded(Backpack, Rifle, 1, state), Assert.Single(result.Events));
        Assert.Equal(state, Get(Backpack).Stacks.Single().State);
    }

    [Fact]
    public void AddItems_DoesNotTopUpStacksWithDifferentState()
    {
        _service.AddItems(Backpack, Water, 1, Worn(40));
        _service.AddItems(Backpack, Water, 1, Worn(40));
        _service.AddItems(Backpack, Water, 1, Worn(90));
        _service.AddItems(Backpack, Water, 1);

        Assert.Equal([2, 1, 1], Get(Backpack).Stacks.Select(s => s.Count));
    }

    [Fact]
    public void AddItems_TreatsEmptyStateAsNoState()
    {
        _service.AddItems(Backpack, Rifle, 1, ItemState.Create());
        _service.AddItems(Backpack, Rifle, 1);

        var stack = Get(Backpack).Stacks.Single();
        Assert.Equal(2, stack.Count);
        Assert.Null(stack.State);
    }

    [Fact]
    public void AddItems_RejectsUnknownAttachedItem()
    {
        var result = _service.AddItems(Backpack, Rifle, 1, Worn(40, new ItemId("base:item/missing")));

        Assert.Equal(InventoryError.UnknownItem, result.Error);
        Assert.Empty(Get(Backpack).Stacks);
    }

    [Fact]
    public void StatefulStack_MassAndVolumeIncludeAttachedItems()
    {
        _service.AddItems(Backpack, Rifle, 1, Worn(40, Scope));

        Assert.Equal(3.5, Get(Backpack).TotalMass.Kilograms, 6);
        Assert.Equal(4.5, Get(Backpack).TotalVolume.Liters, 6);
    }

    [Fact]
    public void AddItems_AppliesLimitsToAttachedItems()
    {
        Assert.Equal(InventoryError.ExceedsMassLimit, _service.AddItems(Crate, Rifle, 1, Worn(40)).Error);
        Assert.Equal(InventoryError.ExceedsVolumeLimit, _service.AddItems(Pouch, Rifle, 1, Worn(40, Scope)).Error);
    }

    [Fact]
    public void MoveItems_CarriesStateToDestination()
    {
        var state = Worn(40);
        var big = new Container(new ContainerId(4), Mass.FromKilograms(50), Volume.FromLiters(50), new ItemCatalog([new ItemDefinition(Rifle, Mass.FromKilograms(3), Volume.FromLiters(4), 5)]));
        _service.AddContainer(big);
        _service.AddItems(Backpack, Rifle, 2, state);
        var stack = Get(Backpack).Stacks.Single();

        var result = _service.MoveItems(Backpack, stack.Id, big.Id, 1);

        Assert.Equal(new ItemsMoved(Backpack, big.Id, Rifle, 1, state), Assert.Single(result.Events));
        Assert.Equal(state, big.Stacks.Single().State);
        Assert.Equal(state, Get(Backpack).Stacks.Single().State);
    }

    [Fact]
    public void SplitStack_KeepsStateOnBothStacksAndEvent()
    {
        var state = Worn(40);
        _service.AddItems(Backpack, Rifle, 2, state);
        var stack = Get(Backpack).Stacks.Single();

        var result = _service.SplitStack(Backpack, stack.Id, 1);

        var split = Assert.IsType<StackSplit>(Assert.Single(result.Events));
        Assert.Equal(state, split.State);
        Assert.All(Get(Backpack).Stacks, s => Assert.Equal(state, s.State));
    }

    [Fact]
    public void MergeStacks_FailsForDifferentStateAndSucceedsForEqualState()
    {
        _service.AddItems(Backpack, Water, 2, Worn(40));
        _service.AddItems(Backpack, Water, 1, Worn(90));
        var stacks = Get(Backpack).Stacks;

        Assert.Equal(InventoryError.StateMismatch, _service.MergeStacks(Backpack, stacks[0].Id, stacks[1].Id).Error);

        var split = ((StackSplit)_service.SplitStack(Backpack, stacks[0].Id, 1).Events.Single()).Created;
        Assert.True(_service.MergeStacks(Backpack, stacks[0].Id, split).IsSuccess);
    }

    [Fact]
    public void MergeStacks_FailsBetweenStatefulAndStatelessStacks()
    {
        _service.AddItems(Backpack, Rifle, 1, Worn(40));
        _service.AddItems(Backpack, Rifle, 1);
        var stacks = Get(Backpack).Stacks;

        Assert.Equal(InventoryError.StateMismatch, _service.MergeStacks(Backpack, stacks[0].Id, stacks[1].Id).Error);
    }

    [Fact]
    public void RemoveItems_OnlyRemovesItemsWithMatchingState()
    {
        _service.AddItems(Backpack, Rifle, 1, Worn(40));
        _service.AddItems(Backpack, Rifle, 1);

        Assert.Equal(InventoryError.InsufficientItems, _service.RemoveItems(Backpack, Rifle, 1, Worn(90)).Error);
        var result = _service.RemoveItems(Backpack, Rifle, 1, Worn(40));

        Assert.Equal(new ItemsRemoved(Backpack, Rifle, 1, Worn(40)), Assert.Single(result.Events));
        Assert.Null(Get(Backpack).Stacks.Single().State);
    }
}