namespace Zombies.Domain.World;

/// <summary>
/// The Structures and Settlement types the game knows, checked against each other once so that planning a Settlement
/// can never meet a missing Structure, an Area with nowhere to get its Area type from, or a layout too big for its Region.
/// </summary>
public sealed class SettlementContent
{
    /// <summary>Open ground around every Structure that is levelled with its floor, so a door always meets flat ground.</summary>
    public const int YardMargin = 2;

    /// <summary>Extra ground kept between the yards of two neighbouring Structures.</summary>
    public const int Gap = 2;

    private readonly Dictionary<string, Structure> _structures = new(StringComparer.Ordinal);

    public SettlementContent(IEnumerable<Structure> structures, IEnumerable<SettlementType> types)
    {
        ArgumentNullException.ThrowIfNull(structures);
        ArgumentNullException.ThrowIfNull(types);
        foreach (var structure in structures)
        {
            if (!_structures.TryAdd(structure.Id, structure))
            {
                throw new ArgumentException($"Structure '{structure.Id}' is defined more than once.", nameof(structures));
            }
        }

        var typeList = types.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < typeList.Count; i++)
        {
            if (typeList[i].Id == typeList[i - 1].Id)
            {
                throw new ArgumentException($"Settlement type '{typeList[i].Id}' is defined more than once.", nameof(types));
            }
        }

        var limit = RegionGrid.Size - (2 * RegionGrid.Margin);
        foreach (var type in typeList)
        {
            var largest = 0;
            foreach (var entry in type.Structures)
            {
                if (!_structures.TryGetValue(entry.Structure, out var structure))
                {
                    throw new ArgumentException($"Settlement type '{type.Id}' names Structure '{entry.Structure}', which is not defined.", nameof(types));
                }

                largest = Math.Max(largest, Math.Max(structure.Width, structure.Depth));
                if (type.AreaTypes.Count == 0 && structure.Areas.Any(a => a.AreaType is null))
                {
                    throw new ArgumentException($"Settlement type '{type.Id}' lists no Area types, but Structure '{structure.Id}' leaves an Area open.", nameof(types));
                }
            }

            var (columns, rows) = GridSize(type.Structures.Sum(s => s.MaxCount));
            var pitch = PitchFor(largest);
            if (columns * pitch > limit || rows * pitch > limit)
            {
                throw new ArgumentException($"Settlement type '{type.Id}' can grow past {limit} blocks, which does not fit in a Region.", nameof(types));
            }
        }

        Types = typeList;
    }

    /// <summary>Settlement types ordered by Content ID.</summary>
    public IReadOnlyList<SettlementType> Types { get; }

    public IReadOnlyCollection<Structure> Structures => _structures.Values;

    public bool TryGetStructure(string id, out Structure structure) => _structures.TryGetValue(id, out structure!);

    /// <summary>Side of one grid cell for Structures no bigger than <paramref name="largestFootprint"/>: the footprint, a yard on each side, and the gap.</summary>
    internal static int PitchFor(int largestFootprint) => largestFootprint + (2 * YardMargin) + Gap;

    /// <summary>The smallest square-ish grid, as columns and rows, that holds <paramref name="count"/> cells.</summary>
    internal static (int Columns, int Rows) GridSize(int count)
    {
        var columns = 1;
        while (columns * columns < count)
        {
            columns++;
        }

        return (columns, (count + columns - 1) / columns);
    }
}
