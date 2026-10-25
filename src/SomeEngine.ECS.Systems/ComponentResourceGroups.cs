using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

internal readonly record struct ComponentResourceIdentity(ResourceKind Kind, int Id, int Version, long Generation);
internal readonly record struct ComponentResourceRange(int Row, long Start, long End, bool Writes);

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
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        int first = 0;
        while (first < ranges.Count)
        {
            long end = ranges[first].End;
            bool writes = ranges[first].Writes;
            int next = first + 1;
            while (next < ranges.Count && ranges[next].Start < end)
            {
                end = Math.Max(end, ranges[next].End);
                writes |= ranges[next].Writes;
                next++;
            }
            if (writes)
                for (int index = first + 1; index < next; index++)
                    Join(ranges[first].Row, ranges[index].Row);
            first = next;
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

    internal int[][] Build()
    {
        var groups = new Dictionary<int, List<int>>();
        for (int row = 0; row < _parents.Length; row++)
        {
            int root = Root(row);
            if (!groups.TryGetValue(root, out List<int>? group)) groups.Add(root, group = []);
            group.Add(row);
        }
        return groups.Values.Select(static group => group.ToArray()).ToArray();
    }

    private int Root(int row)
    {
        int parent;
        while ((parent = Volatile.Read(ref _parents[row])) != row) row = parent;
        return row;
    }
}
