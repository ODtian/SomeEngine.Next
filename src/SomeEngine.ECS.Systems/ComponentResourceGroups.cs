using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

internal readonly record struct ComponentResourceIdentity(ResourceKind Kind, int Id, int Version, long Generation);
internal readonly record struct ComponentResourceRange(int Row, long Start, long End, bool Writes);

internal readonly struct ComponentResourceRows(int[] rows, int[] offsets)
{
    internal static ComponentResourceRows Empty { get; } = new([], [0]);
    internal int Count => offsets.Length - 1;
    internal ReadOnlySpan<int> this[int group] => rows.AsSpan(offsets[group], offsets[group + 1] - offsets[group]);
}

internal sealed class ComponentResourceGroups(int rowCount)
{
    private readonly int[] _parents = Enumerable.Range(0, rowCount).ToArray();

    internal static bool AccessesMatch(ReadOnlySpan<JobResourceAccess> left, ReadOnlySpan<JobResourceAccess> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            JobResourceAccess first = left[index];
            JobResourceAccess second = right[index];
            if (first.Kind != second.Kind || first.Id != second.Id || first.Version != second.Version ||
                first.Generation != second.Generation || first.Mode != second.Mode ||
                first.HasRange != second.HasRange || first.RangeStart != second.RangeStart ||
                first.RangeLength != second.RangeLength) return false;
        }
        return true;
    }

    internal static void AddAccess(JobResourceAccess access, int row,
        Dictionary<ComponentResourceIdentity, List<ComponentResourceRange>> buckets)
    {
        var identity = new ComponentResourceIdentity(access.Kind, access.Id, access.Version, access.Generation);
        if (!buckets.TryGetValue(identity, out List<ComponentResourceRange>? ranges))
            buckets.Add(identity, ranges = []);
        ranges.Add(new(row, access.HasRange ? access.RangeStart : 0,
            access.HasRange ? access.RangeStart + access.RangeLength : long.MaxValue,
            access.Mode != JobAccessMode.Read));
    }

    internal void JoinRanges(List<ComponentResourceRange> ranges)
    {
        if (!ranges.Exists(static range => range.Writes)) return;
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var ends = new PriorityQueue<int, long>();
        HashSet<int> reads = [], writes = [];
        for (int index = 0; index < ranges.Count; index++)
        {
            ComponentResourceRange current = ranges[index];
            while (ends.TryPeek(out int ended, out long end) && end <= current.Start)
            {
                ends.Dequeue();
                (ranges[ended].Writes ? writes : reads).Remove(ended);
            }
            foreach (int previous in writes) Join(current.Row, ranges[previous].Row);
            if (current.Writes)
                foreach (int previous in reads) Join(current.Row, ranges[previous].Row);
            (current.Writes ? writes : reads).Add(index);
            ends.Enqueue(index, current.End);
        }
    }

    internal void Join(int left, int right)
    {
        while (true)
        {
            left = Root(left);
            right = Root(right);
            if (left == right) return;
            int lower = Math.Min(left, right);
            int higher = Math.Max(left, right);
            if (Interlocked.CompareExchange(ref _parents[higher], lower, higher) == higher) return;
        }
    }

    internal ComponentResourceRows Build()
    {
        var counts = new int[_parents.Length];
        int groupCount = 0;
        for (int row = 0; row < _parents.Length; row++)
        {
            int root = Root(row);
            _parents[row] = root;
            if (counts[root]++ == 0) groupCount++;
        }
        var offsets = new int[groupCount + 1];
        int group = 0, start = 0;
        for (int root = 0; root < counts.Length; root++)
        {
            int count = counts[root];
            if (count == 0) continue;
            offsets[group++] = start;
            counts[root] = start;
            start += count;
        }
        offsets[groupCount] = _parents.Length;
        var rows = new int[_parents.Length];
        for (int row = 0; row < rows.Length; row++) rows[counts[_parents[row]]++] = row;
        return new(rows, offsets);
    }

    private int Root(int row)
    {
        int parent;
        while ((parent = Volatile.Read(ref _parents[row])) != row) row = parent;
        return row;
    }
}
