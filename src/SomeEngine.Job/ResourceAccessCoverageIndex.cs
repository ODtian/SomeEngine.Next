namespace SomeEngine.Job;

/// <summary>
/// Immutable-for-one-registration lookup used by current-job capability checks. Large packet
/// owners often declare hundreds of disjoint ranges; grouping them by resource identity and
/// merging each mode's intervals avoids rescanning the complete declaration set for every work
/// item while retaining exact union coverage semantics.
/// </summary>
internal sealed class ResourceAccessCoverageIndex
{
    private readonly Dictionary<ResourceAccessIdentity, CoverageSet> _sets = new();
    private readonly Stack<CoverageSet> _pool = new();

    internal void Build(AccessBuilder<ResourceManager.ActiveResourceAccess> accesses)
    {
        Clear();
        for (int i = 0; i < accesses.Count; i++)
        {
            JobResourceAccess access = accesses.Get(i).Access;
            var identity = new ResourceAccessIdentity(
                access.Kind,
                access.Id,
                access.Version,
                access.Generation);
            if (!_sets.TryGetValue(identity, out CoverageSet? set))
            {
                set = _pool.Count == 0 ? new CoverageSet() : _pool.Pop();
                _sets.Add(identity, set);
            }
            set.Add(access);
        }

        foreach (CoverageSet set in _sets.Values)
            set.Seal();
    }

    internal bool Covers(JobResourceAccess required)
    {
        var identity = new ResourceAccessIdentity(
            required.Kind,
            required.Id,
            required.Version,
            required.Generation);
        return _sets.TryGetValue(identity, out CoverageSet? set) &&
            set.Covers(required);
    }

    internal void Clear()
    {
        foreach (CoverageSet set in _sets.Values)
        {
            set.Clear();
            _pool.Push(set);
        }
        _sets.Clear();
    }

    private sealed class CoverageSet
    {
        private readonly List<CoverageRange> _read = [];
        private readonly List<CoverageRange> _write = [];
        private readonly List<CoverageRange> _exclusive = [];
        private bool _wholeRead;
        private bool _wholeWrite;
        private bool _wholeExclusive;

        internal void Add(JobResourceAccess access)
        {
            if (!access.HasRange)
            {
                AddWhole(access.Mode);
                return;
            }

            var range = new CoverageRange(
                access.RangeStart,
                checked(access.RangeStart + access.RangeLength));
            _read.Add(range);
            if (access.Mode is JobAccessMode.Write or JobAccessMode.Exclusive)
                _write.Add(range);
            if (access.Mode == JobAccessMode.Exclusive)
                _exclusive.Add(range);
        }

        internal void Seal()
        {
            Merge(_read);
            Merge(_write);
            Merge(_exclusive);
        }

        internal bool Covers(JobResourceAccess required)
        {
            (bool whole, List<CoverageRange> ranges) = required.Mode switch
            {
                JobAccessMode.Read => (_wholeRead, _read),
                JobAccessMode.Write => (_wholeWrite, _write),
                JobAccessMode.Exclusive => (_wholeExclusive, _exclusive),
                _ => throw new InvalidOperationException("Job resource access mode is invalid."),
            };
            if (whole)
                return true;
            if (!required.HasRange)
                return false;

            return Contains(
                ranges,
                required.RangeStart,
                checked(required.RangeStart + required.RangeLength));
        }

        internal void Clear()
        {
            _read.Clear();
            _write.Clear();
            _exclusive.Clear();
            _wholeRead = false;
            _wholeWrite = false;
            _wholeExclusive = false;
        }

        private void AddWhole(JobAccessMode mode)
        {
            _wholeRead = true;
            if (mode is JobAccessMode.Write or JobAccessMode.Exclusive)
                _wholeWrite = true;
            if (mode == JobAccessMode.Exclusive)
                _wholeExclusive = true;
        }

        private static bool Contains(
            List<CoverageRange> ranges,
            long requiredStart,
            long requiredEnd)
        {
            int low = 0;
            int high = ranges.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (ranges[middle].Start <= requiredStart)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            return high >= 0 && ranges[high].End >= requiredEnd;
        }

        private static void Merge(List<CoverageRange> ranges)
        {
            if (ranges.Count <= 1)
                return;

            ranges.Sort(static (left, right) =>
            {
                int start = left.Start.CompareTo(right.Start);
                return start != 0 ? start : left.End.CompareTo(right.End);
            });
            int write = 0;
            CoverageRange current = ranges[0];
            for (int read = 1; read < ranges.Count; read++)
            {
                CoverageRange next = ranges[read];
                if (next.Start <= current.End)
                {
                    if (next.End > current.End)
                        current = new CoverageRange(current.Start, next.End);
                    continue;
                }

                ranges[write++] = current;
                current = next;
            }
            ranges[write++] = current;
            if (write < ranges.Count)
                ranges.RemoveRange(write, ranges.Count - write);
        }
    }

    private readonly record struct ResourceAccessIdentity(
        ResourceKind Kind,
        int Id,
        int Version,
        long Generation);

    private readonly record struct CoverageRange(long Start, long End);
}
