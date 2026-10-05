using Zombies.Domain.Mods;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// The number each mod message travels under. Numbers are assigned, never chosen by a mod, from the list of mods the Server
/// runs (every mod but client-only ones) sorted by mod id: the mod at index <c>i</c> of that list owns the block of
/// <see cref="MaxPerMod"/> numbers starting at <c>FirstNumber + i * MaxPerMod</c>, and its messages take that block in the
/// order of their Content IDs. A join already requires the same mods with the same content on both sides, so a client
/// and its Server number every message the same, whatever order the mods were installed or loaded in, and even though a
/// client never runs a Server mod's code.
/// </summary>
public sealed class ModMessageTable
{
    /// <summary>The first number a mod message may take. Smaller ones are the game's own Domain commands.</summary>
    public const ushort FirstNumber = 1024;

    /// <summary>How many messages one mod may register.</summary>
    public const int MaxPerMod = 64;

    public static readonly ModMessageTable Empty = new([]);

    private readonly Dictionary<string, ushort> _numbers;

    private ModMessageTable(Dictionary<string, ushort> numbers) => _numbers = numbers;

    /// <summary>Every message with its number, in number order.</summary>
    public IReadOnlyList<KeyValuePair<string, ushort>> All => [.. _numbers.OrderBy(n => n.Value)];

    public bool TryGetNumber(string messageId, out ushort number) => _numbers.TryGetValue(messageId, out number);

    internal ushort NumberOf(string messageId) => _numbers[messageId];

    internal static ModMessageTable Assign(ModLoadResult mods, IReadOnlyDictionary<string, IReadOnlyList<string>> messagesByMod, List<CodeModProblem> problems)
    {
        var networked = mods.Mods
            .Where(m => m.Manifest.Side != ModSide.Client)
            .Select(m => m.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        var numbers = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var (modId, messages) in messagesByMod.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (messages.Count == 0)
            {
                continue;
            }

            var index = networked.IndexOf(modId);
            if (index < 0)
            {
                problems.Add(new CodeModProblem(modId, "A client-only mod cannot register a message: the Server never loads it, so nothing would receive it. Declare the mod's side as 'both'."));
                continue;
            }

            if (messages.Count > MaxPerMod)
            {
                problems.Add(new CodeModProblem(modId, $"Registers {messages.Count} messages; a mod may register at most {MaxPerMod}."));
                continue;
            }

            var first = FirstNumber + (index * MaxPerMod);
            if (first + MaxPerMod - 1 > ushort.MaxValue)
            {
                problems.Add(new CodeModProblem(modId, "Too many mods run on the Server for this one's messages to have numbers."));
                continue;
            }

            var ordered = messages.Order(StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                numbers[ordered[i]] = (ushort)(first + i);
            }
        }

        return new ModMessageTable(numbers);
    }
}
