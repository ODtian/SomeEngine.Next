namespace SomeEngine.RenderGraph;

/// <summary>Direct bit lookup for transitive happens-before between live passes.</summary>
internal readonly unsafe struct ReachabilityTable
{
    private readonly int* _positions;
    private readonly ulong* _ancestors;
    private readonly int _positionCount;
    private readonly int _wordCount;
    private readonly bool _totalOrder;

    public ReachabilityTable(
        RenderGraph graph,
        ArenaSlice<int> activePassOrdinals,
        ArenaSlice<QueueType> queues)
    {
        ActivePassOrdinals = activePassOrdinals;
        ArenaSlice<int> positionStorage =
            graph.AllocateSlice<int>(graph.Passes.Length, clear: false);
        _positions = positionStorage.DangerousPointer;
        _positionCount = positionStorage.Length;
        positionStorage.Span.Fill(-1);
        for (int index = 0; index < activePassOrdinals.Length; index++)
            _positions[activePassOrdinals[index]] = index;
        _totalOrder = HasOneQueue(activePassOrdinals, queues);
        if (_totalOrder)
        {
            _wordCount = 0;
            _ancestors = null;
            return;
        }
        _wordCount = (activePassOrdinals.Length + 63) >> 6;
        ArenaSlice<ulong> ancestorStorage =
            graph.AllocateSlice<ulong>(
                checked(activePassOrdinals.Length * _wordCount));
        _ancestors = ancestorStorage.DangerousPointer;
        ArenaSlice<int> lastOnQueue = graph.AllocateSlice<int>(3, clear: false);
        lastOnQueue.Span.Fill(-1);
        for (int current = 0; current < activePassOrdinals.Length; current++)
        {
            int pass = activePassOrdinals[current];
            foreach (int predecessorPass in graph.GetPassDependencies(pass))
                AddPredecessor(current, predecessorPass);
            int queuePredecessor = lastOnQueue[(int)queues[pass]];
            if (queuePredecessor >= 0) AddPredecessor(current, queuePredecessor);
            lastOnQueue[(int)queues[pass]] = pass;
        }
    }

    public ArenaSlice<int> ActivePassOrdinals { get; }

    public bool IsTotalOrder => _totalOrder;

    public int WordCount => _wordCount;

    public bool Before(int leftPass, int rightPass)
    {
        int left = Position(leftPass);
        int right = Position(rightPass);
        return _totalOrder
            ? left >= 0 && right >= 0 && left < right
            : left >= 0 && right >= 0 &&
            (_ancestors[right * _wordCount + (left >> 6)] & (1UL << (left & 63))) != 0;
    }

    public int CompareUses(ReadOnlySpan<int> leftUses, ReadOnlySpan<int> rightUses)
    {
        if (_totalOrder)
        {
            if (leftUses.IsEmpty || rightUses.IsEmpty) return 0;
            if (Position(leftUses[^1]) < Position(rightUses[0])) return -1;
            if (Position(rightUses[^1]) < Position(leftUses[0])) return 1;
            return 0;
        }
        if (AllBefore(leftUses, rightUses)) return -1;
        if (AllBefore(rightUses, leftUses)) return 1;
        return 0;
    }

    public int WriteOrderedFrontier(
        ReadOnlySpan<int> uses,
        Span<int> destination,
        bool start)
    {
        if (destination.Length < uses.Length)
            throw new ArgumentException(
                "Frontier destination is smaller than the use set.",
                nameof(destination));
        if (uses.IsEmpty) return 0;
        if (_totalOrder)
        {
            destination[0] = start ? uses[0] : uses[^1];
            return 1;
        }

        int previousPosition = -1;
        for (int index = 0; index < uses.Length; index++)
        {
            int position = Position(uses[index]);
            if (position <= previousPosition)
                throw new InvalidOperationException(
                    "Resource uses must follow active topological order.");
            previousPosition = position;
        }

        int count = 0;
        for (int candidateIndex = 0;
             candidateIndex < uses.Length;
             candidateIndex++)
        {
            int candidatePosition = _positions[uses[candidateIndex]];
            bool covered = false;
            if (start)
            {
                ulong* ancestors =
                    _ancestors + candidatePosition * _wordCount;
                for (int otherIndex = 0;
                     otherIndex < candidateIndex;
                     otherIndex++)
                {
                    int otherPosition = _positions[uses[otherIndex]];
                    if ((ancestors[otherPosition >> 6] &
                         (1UL << (otherPosition & 63))) == 0)
                    {
                        continue;
                    }
                    covered = true;
                    break;
                }
            }
            else
            {
                for (int otherIndex = candidateIndex + 1;
                     otherIndex < uses.Length;
                     otherIndex++)
                {
                    int otherPosition = _positions[uses[otherIndex]];
                    ulong* ancestors =
                        _ancestors + otherPosition * _wordCount;
                    if ((ancestors[candidatePosition >> 6] &
                         (1UL << (candidatePosition & 63))) == 0)
                    {
                        continue;
                    }
                    covered = true;
                    break;
                }
            }
            if (!covered) destination[count++] = uses[candidateIndex];
        }
        return count;
    }

    public void WritePositionMask(
        ReadOnlySpan<int> passes,
        Span<ulong> destination)
    {
        if (_totalOrder || destination.Length != _wordCount)
            throw new ArgumentException(
                "Position masks require the exact non-total reachability width.",
                nameof(destination));
        destination.Clear();
        foreach (int pass in passes)
        {
            int position = Position(pass);
            if (position < 0)
                throw new InvalidOperationException(
                    "A frontier pass is not active.");
            destination[position >> 6] |=
                1UL << (position & 63);
        }
    }

    public void WriteCommonAncestorMask(
        ReadOnlySpan<int> passes,
        Span<ulong> destination)
    {
        if (_totalOrder || destination.Length != _wordCount)
            throw new ArgumentException(
                "Ancestor masks require the exact non-total reachability width.",
                nameof(destination));
        if (passes.IsEmpty)
        {
            destination.Clear();
            return;
        }
        int first = Position(passes[0]);
        if (first < 0)
            throw new InvalidOperationException(
                "A frontier pass is not active.");
        new ReadOnlySpan<ulong>(
                _ancestors + first * _wordCount,
                _wordCount)
            .CopyTo(destination);
        for (int index = 1; index < passes.Length; index++)
        {
            int position = Position(passes[index]);
            if (position < 0)
                throw new InvalidOperationException(
                    "A frontier pass is not active.");
            ulong* ancestors =
                _ancestors + position * _wordCount;
            for (int word = 0; word < _wordCount; word++)
                destination[word] &= ancestors[word];
        }
    }

    public ArenaSlice<int> StartFrontier(RenderGraph graph, ReadOnlySpan<int> uses)
    {
        if (_totalOrder) return Single(graph, uses, first: true);
        int count = 0;
        for (int candidateIndex = 0; candidateIndex < uses.Length; candidateIndex++)
        {
            int candidate = uses[candidateIndex];
            bool preceded = false;
            for (int otherIndex = 0; otherIndex < uses.Length; otherIndex++)
            {
                int other = uses[otherIndex];
                if (other != candidate && Before(other, candidate))
                {
                    preceded = true;
                    break;
                }
            }
            if (!preceded) count++;
        }
        ArenaSlice<int> result = graph.AllocateSlice<int>(count, clear: false);
        int destination = 0;
        for (int candidateIndex = 0; candidateIndex < uses.Length; candidateIndex++)
        {
            int candidate = uses[candidateIndex];
            bool preceded = false;
            for (int otherIndex = 0; otherIndex < uses.Length; otherIndex++)
            {
                int other = uses[otherIndex];
                if (other != candidate && Before(other, candidate))
                {
                    preceded = true;
                    break;
                }
            }
            if (!preceded) result[destination++] = candidate;
        }
        result.Span.Sort();
        return result;
    }

    public ArenaSlice<int> EndFrontier(RenderGraph graph, ReadOnlySpan<int> uses)
    {
        if (_totalOrder) return Single(graph, uses, first: false);
        int count = 0;
        for (int candidateIndex = 0; candidateIndex < uses.Length; candidateIndex++)
        {
            int candidate = uses[candidateIndex];
            bool followed = false;
            for (int otherIndex = 0; otherIndex < uses.Length; otherIndex++)
            {
                int other = uses[otherIndex];
                if (other != candidate && Before(candidate, other))
                {
                    followed = true;
                    break;
                }
            }
            if (!followed) count++;
        }
        ArenaSlice<int> result = graph.AllocateSlice<int>(count, clear: false);
        int destination = 0;
        for (int candidateIndex = 0; candidateIndex < uses.Length; candidateIndex++)
        {
            int candidate = uses[candidateIndex];
            bool followed = false;
            for (int otherIndex = 0; otherIndex < uses.Length; otherIndex++)
            {
                int other = uses[otherIndex];
                if (other != candidate && Before(candidate, other))
                {
                    followed = true;
                    break;
                }
            }
            if (!followed) result[destination++] = candidate;
        }
        result.Span.Sort();
        return result;
    }

    private void AddPredecessor(int current, int predecessorPass)
    {
        int predecessor = Position(predecessorPass);
        if (predecessor < 0 || predecessor >= current)
            throw new InvalidOperationException("Pass order contains a non-topological dependency.");
        int currentOffset = current * _wordCount;
        int predecessorOffset = predecessor * _wordCount;
        _ancestors[currentOffset + (predecessor >> 6)] |= 1UL << (predecessor & 63);
        for (int word = 0; word < _wordCount; word++)
            _ancestors[currentOffset + word] |= _ancestors[predecessorOffset + word];
    }

    public bool AllBefore(ReadOnlySpan<int> leftUses, ReadOnlySpan<int> rightUses)
    {
        if (leftUses.IsEmpty || rightUses.IsEmpty) return false;
        if (_totalOrder)
            return Position(leftUses[^1]) < Position(rightUses[0]);
        foreach (int leftPass in leftUses)
        {
            int left = Position(leftPass);
            if (left < 0) return false;
            ulong mask = 1UL << (left & 63);
            int word = left >> 6;
            foreach (int rightPass in rightUses)
            {
                int right = Position(rightPass);
                if (right < 0 ||
                    (_ancestors[right * _wordCount + word] & mask) == 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private int Position(int pass) =>
        (uint)pass < (uint)_positionCount ? _positions[pass] : -1;

    private static bool HasOneQueue(
        ArenaSlice<int> activePassOrdinals,
        ArenaSlice<QueueType> queues)
    {
        if (activePassOrdinals.IsEmpty) return true;
        QueueType queue = queues[activePassOrdinals[0]];
        for (int index = 1; index < activePassOrdinals.Length; index++)
            if (queues[activePassOrdinals[index]] != queue) return false;
        return true;
    }

    private static ArenaSlice<int> Single(
        RenderGraph graph,
        ReadOnlySpan<int> uses,
        bool first)
    {
        if (uses.IsEmpty) return default;
        ArenaSlice<int> result = graph.AllocateSlice<int>(1, clear: false);
        result[0] = first ? uses[0] : uses[^1];
        return result;
    }
}
