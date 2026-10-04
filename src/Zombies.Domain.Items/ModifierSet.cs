namespace Zombies.Domain.Items;

/// <summary>
/// The Modifiers acting on one owner, such as a weapon or a creature, and the effective value of each Stat they change.
/// </summary>
/// <remarks>
/// The effective value of a Stat is computed in this fixed order:
/// <list type="number">
/// <item>Start from the base value.</item>
/// <item>Add the value of every <see cref="ModifierOperation.Add"/> Modifier on that Stat, smallest first.</item>
/// <item>Multiply by the value of every <see cref="ModifierOperation.Multiply"/> Modifier on that Stat, smallest first.</item>
/// </list>
/// Taking values smallest first means the order Modifiers were added never changes the result, not even through floating-point rounding.
/// </remarks>
public sealed class ModifierSet
{
    private readonly List<Modifier> _modifiers = [];

    /// <summary>Every Modifier in the order it was added.</summary>
    public IReadOnlyList<Modifier> All => _modifiers.AsReadOnly();

    /// <summary>Adds a Modifier. Adding an equal Modifier again applies it again.</summary>
    public void Add(Modifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        _modifiers.Add(modifier);
    }

    /// <summary>Removes every Modifier granted by <paramref name="source"/>.</summary>
    /// <returns>How many Modifiers were removed.</returns>
    public int RemoveSource(ModifierSource source) => _modifiers.RemoveAll(m => m.Source == source);

    /// <summary>Applies every Modifier on <paramref name="stat"/> to <paramref name="baseValue"/>, in the order described on <see cref="ModifierSet"/>.</summary>
    public double EffectiveValue(StatName stat, double baseValue)
    {
        var added = ValuesOf(stat, ModifierOperation.Add).Aggregate(baseValue, (total, value) => total + value);
        return ValuesOf(stat, ModifierOperation.Multiply).Aggregate(added, (total, value) => total * value);
    }

    private IEnumerable<double> ValuesOf(StatName stat, ModifierOperation operation) =>
        _modifiers.Where(m => m.Stat == stat && m.Operation == operation).Select(m => m.Value).Order();
}
