namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private static void MergeResourceRanges(ref AccessBuilder<ActiveResourceAccess> accesses)
    {
        var groups = new Dictionary<ResourceAccessMode, ResourceAccessRanges>();
        for (int index = 0; index < accesses.Count; index++)
        {
            ActiveResourceAccess access = accesses.Get(index);
            var key = new ResourceAccessMode(access.State, access.Access.Mode);
            if (!groups.TryGetValue(key, out ResourceAccessRanges? ranges))
                groups.Add(key, ranges = new(access));
            ranges.Add(access.Access);
        }

        AccessBuilder<ActiveResourceAccess> merged = new();
        try
        {
            foreach (ResourceAccessRanges ranges in groups.Values)
                ranges.AppendTo(ref merged);
        }
        catch
        {
            merged.Clear();
            throw;
        }
        accesses.Clear();
        accesses = merged;
    }

    private readonly record struct ResourceAccessMode(ResourceState Resource, JobAccessMode Mode);
    private readonly record struct ResourceRange(long Start, long End);

    private sealed class ResourceAccessRanges(ActiveResourceAccess first)
    {
        private readonly List<ResourceRange> _ranges = [];
        private bool _hasWholeAccess;
        private JobResourceAccess _wholeAccess;

        internal void Add(JobResourceAccess access)
        {
            if (_hasWholeAccess) return;
            if (!access.HasRange)
            {
                _hasWholeAccess = true;
                _wholeAccess = access;
                _ranges.Clear();
                return;
            }

            var range = new ResourceRange(access.RangeStart, access.RangeStart + access.RangeLength);
            if (_ranges.Count != 0)
            {
                ResourceRange previous = _ranges[^1];
                if (range.Start >= previous.Start && range.Start <= previous.End)
                {
                    _ranges[^1] = new(previous.Start, Math.Max(previous.End, range.End));
                    return;
                }
            }
            _ranges.Add(range);
        }

        internal void AppendTo(ref AccessBuilder<ActiveResourceAccess> accesses)
        {
            if (_hasWholeAccess)
            {
                accesses.Add(new(first.Owner, _wholeAccess, first.State));
                return;
            }
            _ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            ResourceRange current = _ranges[0];
            for (int index = 1; index < _ranges.Count; index++)
            {
                ResourceRange next = _ranges[index];
                if (next.Start <= current.End)
                    current = new(current.Start, Math.Max(current.End, next.End));
                else
                {
                    AppendRange(current, ref accesses);
                    current = next;
                }
            }
            AppendRange(current, ref accesses);
        }

        private void AppendRange(ResourceRange range, ref AccessBuilder<ActiveResourceAccess> accesses) =>
            accesses.Add(new(first.Owner, first.Access.WithRange(range.Start, range.End - range.Start), first.State));
    }
}
