using System.Text.Json.Serialization;
using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

/// <summary>Anything alive in the world that has a body: a player, a zombie, an animal, or an NPC.</summary>
public readonly record struct CreatureId(long Value)
{
    public override string ToString() => $"creature-{Value}";
}

[JsonConverter(typeof(EffectCategoryJsonConverter))]
public enum EffectCategory
{
    [JsonStringEnumMemberName("ailment")]
    Ailment,

    [JsonStringEnumMemberName("buff")]
    Buff,

    [JsonStringEnumMemberName("environmental")]
    Environmental,
}

public sealed class EffectCategoryJsonConverter() : JsonStringEnumConverter<EffectCategory>(namingPolicy: null, allowIntegerValues: false);

/// <summary>What happens when an effect is applied to a creature that already has it.</summary>
[JsonConverter(typeof(StackingRuleJsonConverter))]
public enum StackingRule
{
    /// <summary>The duration starts over.</summary>
    [JsonStringEnumMemberName("refresh")]
    Refresh,

    /// <summary>The duration is added to what remains.</summary>
    [JsonStringEnumMemberName("extend")]
    Extend,

    /// <summary>Another stack is added, up to the effect's limit, and the duration starts over.</summary>
    [JsonStringEnumMemberName("stack")]
    Stack,

    /// <summary>Nothing changes.</summary>
    [JsonStringEnumMemberName("ignore")]
    Ignore,
}

public sealed class StackingRuleJsonConverter() : JsonStringEnumConverter<StackingRule>(namingPolicy: null, allowIntegerValues: false);

/// <summary>A change to one Stat while the effect is active. It is applied once per stack.</summary>
public sealed record EffectModifier
{
    public EffectModifier(StatName stat, ModifierOperation operation, double value)
    {
        _ = new Modifier(stat, operation, value, new ModifierSource("validation"));
        Stat = stat;
        Operation = operation;
        Value = value;
    }

    public StatName Stat { get; }

    public ModifierOperation Operation { get; }

    public double Value { get; }
}

/// <summary>A change other contexts apply on a schedule, such as damage. Effects only announce it; they never apply it.</summary>
public sealed record PeriodicChange
{
    public PeriodicChange(StatName change, TimeSpan every, double amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero);
        if (!double.IsFinite(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An amount must be a finite number.");
        }

        Change = change;
        Every = every;
        Amount = amount;
    }

    public StatName Change { get; }

    public TimeSpan Every { get; }

    public double Amount { get; }
}

/// <summary>A phase of an effect that starts once the effect has been active for <see cref="After"/>, such as infection worsening.</summary>
public sealed record EffectStage
{
    public EffectStage(string name, TimeSpan after, IEnumerable<EffectModifier>? modifiers = null, IEnumerable<PeriodicChange>? periodic = null)
    {
        if (!StatName.TryParse(name, out _))
        {
            throw new ArgumentException($"'{name}' is not a valid stage name (expected lowercase letters, digits and '_').", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(after, TimeSpan.Zero);
        Name = name;
        After = after;
        Modifiers = [.. modifiers ?? []];
        Periodic = [.. periodic ?? []];
    }

    public string Name { get; }

    public TimeSpan After { get; }

    public IReadOnlyList<EffectModifier> Modifiers { get; }

    public IReadOnlyList<PeriodicChange> Periodic { get; }
}

/// <summary>Definition of a Status effect.</summary>
public sealed record StatusEffectDefinition
{
    public StatusEffectDefinition(
        string id,
        EffectCategory category,
        TimeSpan? duration = null,
        StackingRule stacking = StackingRule.Refresh,
        int maxStacks = 1,
        IEnumerable<EffectStage>? stages = null,
        IEnumerable<EffectModifier>? modifiers = null,
        IEnumerable<PeriodicChange>? periodic = null,
        IEnumerable<ItemId>? curedByItems = null,
        IEnumerable<string>? curedByEffects = null)
    {
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown effect category.");
        }

        if (!Enum.IsDefined(stacking))
        {
            throw new ArgumentOutOfRangeException(nameof(stacking), stacking, "Unknown stacking rule.");
        }

        if (duration is { } d)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(d, TimeSpan.Zero);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxStacks, 1);

        Stages = [.. stages ?? []];
        for (var i = 1; i < Stages.Count; i++)
        {
            if (Stages[i].After <= Stages[i - 1].After)
            {
                throw new ArgumentException($"Stages of '{id}' must start at strictly increasing times.", nameof(stages));
            }
        }

        CuredByEffects = [.. (curedByEffects ?? []).Select(e => ItemId.TryParse(e, out _) ? e : throw new ArgumentException($"'{e}' is not a valid Content ID.", nameof(curedByEffects)))];
        Id = id;
        Category = category;
        Duration = duration;
        Stacking = stacking;
        MaxStacks = maxStacks;
        Modifiers = [.. modifiers ?? []];
        Periodic = [.. periodic ?? []];
        CuredByItems = [.. curedByItems ?? []];
    }

    public string Id { get; }

    public EffectCategory Category { get; }

    /// <summary>How long the effect lasts, or null if it lasts until removed or cured. Reaching the end is how time cures an effect.</summary>
    public TimeSpan? Duration { get; }

    public StackingRule Stacking { get; }

    public int MaxStacks { get; }

    public IReadOnlyList<EffectStage> Stages { get; }

    public IReadOnlyList<EffectModifier> Modifiers { get; }

    public IReadOnlyList<PeriodicChange> Periodic { get; }

    public IReadOnlyList<ItemId> CuredByItems { get; }

    /// <summary>Effects that cure this one when they are applied.</summary>
    public IReadOnlyList<string> CuredByEffects { get; }
}

/// <summary>The Status effects the game knows.</summary>
public sealed class StatusEffectCatalog
{
    private readonly Dictionary<string, StatusEffectDefinition> _definitions = [];

    public StatusEffectCatalog(IEnumerable<StatusEffectDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Status effect '{definition.Id}' is defined more than once.", nameof(definitions));
            }
        }

        foreach (var definition in _definitions.Values)
        {
            foreach (var cure in definition.CuredByEffects.Where(c => !_definitions.ContainsKey(c)))
            {
                throw new ArgumentException($"Status effect '{definition.Id}' is cured by unknown effect '{cure}'.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<StatusEffectDefinition> All => _definitions.Values;

    public bool TryGet(string id, out StatusEffectDefinition definition) => _definitions.TryGetValue(id, out definition!);
}
