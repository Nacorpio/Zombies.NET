namespace Zombies.Engine.Render;

/// <summary>
/// Hands out ranges of a fixed-size space, such as parts of one big GPU buffer, and takes them back. Free neighbours merge,
/// so memory does not fragment into unusable crumbs. Ranges are counted in whatever unit the caller likes (vertices, indices, bytes).
/// This is only bookkeeping; the actual memory lives elsewhere.
/// </summary>
public sealed class RangeAllocator
{
    private readonly List<(int Start, int Length)> _free;

    public RangeAllocator(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _free = [(0, capacity)];
    }

    public int Capacity { get; }

    public int AllocationCount { get; private set; }

    public int UsedCount => Capacity - FreeAmount;

    /// <summary>Largest single range that could be handed out right now.</summary>
    public int LargestFree => _free.Count == 0 ? 0 : _free.Max(r => r.Length);

    private int FreeAmount => _free.Sum(r => r.Length);

    /// <summary>Takes <paramref name="length"/> units from the first free range that fits. Returns -1 when there is no room.</summary>
    public int Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        for (var i = 0; i < _free.Count; i++)
        {
            var (start, available) = _free[i];
            if (available < length)
            {
                continue;
            }

            if (available == length)
            {
                _free.RemoveAt(i);
            }
            else
            {
                _free[i] = (start + length, available - length);
            }

            AllocationCount++;
            return start;
        }

        return -1;
    }

    /// <summary>Returns a range obtained from <see cref="Allocate"/>. Giving back something that is already free is an error.</summary>
    public void Free(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (start < 0 || start + length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "The range lies outside the space.");
        }

        var index = _free.FindIndex(r => r.Start > start);
        if (index < 0)
        {
            index = _free.Count;
        }

        if (index > 0 && _free[index - 1].Start + _free[index - 1].Length > start)
        {
            throw new InvalidOperationException("The range overlaps one that is already free.");
        }

        if (index < _free.Count && start + length > _free[index].Start)
        {
            throw new InvalidOperationException("The range overlaps one that is already free.");
        }

        _free.Insert(index, (start, length));

        // Merge with the following range, then the previous one.
        if (index + 1 < _free.Count && _free[index].Start + _free[index].Length == _free[index + 1].Start)
        {
            _free[index] = (_free[index].Start, _free[index].Length + _free[index + 1].Length);
            _free.RemoveAt(index + 1);
        }

        if (index > 0 && _free[index - 1].Start + _free[index - 1].Length == _free[index].Start)
        {
            _free[index - 1] = (_free[index - 1].Start, _free[index - 1].Length + _free[index].Length);
            _free.RemoveAt(index);
        }

        AllocationCount--;
    }
}
