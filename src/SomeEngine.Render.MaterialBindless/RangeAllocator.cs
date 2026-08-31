namespace SomeEngine.Render;

internal sealed class RangeAllocator
{
    private readonly uint _capacity;
    private readonly List<Range> _free;

    internal RangeAllocator(uint capacity)
    {
        if (capacity == 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _free = [new Range(0, capacity)];
    }

    internal uint Allocate(uint count)
    {
        if (count == 0)
            return 0;
        for (int index = 0; index < _free.Count; index++)
        {
            Range range = _free[index];
            if (range.Count < count)
                continue;
            uint result = range.Start;
            if (range.Count == count)
                _free.RemoveAt(index);
            else
                _free[index] = new Range(checked(range.Start + count), range.Count - count);
            return result;
        }

        throw new InvalidOperationException(
            $"MaterialBindless has exhausted its {_capacity} configured value slots.");
    }

    internal void Free(uint start, uint count)
    {
        if (count == 0)
            return;
        if (start >= _capacity || count > _capacity - start)
            throw new ArgumentOutOfRangeException(nameof(count));

        int insertion = 0;
        while (insertion < _free.Count && _free[insertion].Start < start)
            insertion++;
        if (insertion > 0)
        {
            Range previous = _free[insertion - 1];
            if (start < checked(previous.Start + previous.Count))
                throw new InvalidOperationException("A MaterialBindless value range was freed twice.");
        }
        if (insertion < _free.Count && checked(start + count) > _free[insertion].Start)
            throw new InvalidOperationException("A MaterialBindless value range was freed twice.");

        _free.Insert(insertion, new Range(start, count));
        if (insertion > 0)
        {
            Range previous = _free[insertion - 1];
            Range current = _free[insertion];
            if (checked(previous.Start + previous.Count) == current.Start)
            {
                _free[insertion - 1] = new Range(previous.Start, checked(previous.Count + current.Count));
                _free.RemoveAt(insertion);
                insertion--;
            }
        }
        if (insertion + 1 < _free.Count)
        {
            Range current = _free[insertion];
            Range next = _free[insertion + 1];
            if (checked(current.Start + current.Count) == next.Start)
            {
                _free[insertion] = new Range(current.Start, checked(current.Count + next.Count));
                _free.RemoveAt(insertion + 1);
            }
        }
    }

    private readonly record struct Range(uint Start, uint Count);
}

internal sealed class DisposeGroup : IDisposable
{
    private IDisposable?[]? _values;

    internal DisposeGroup(params IDisposable?[] values) => _values = values;

    public void Dispose()
    {
        IDisposable?[]? values = Interlocked.Exchange(ref _values, null);
        if (values is null)
            return;
        List<Exception>? failures = null;
        for (int index = values.Length - 1; index >= 0; index--)
        {
            try
            {
                values[index]?.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null)
            throw new AggregateException(failures);
    }
}

internal sealed class DisposeAction : IDisposable
{
    private Action? _action;

    internal DisposeAction(Action action) => _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
