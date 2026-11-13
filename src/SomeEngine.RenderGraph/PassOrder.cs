namespace SomeEngine.RenderGraph;

// Retains execution positions. Repairing a dependency reorders only the nodes
// participating in its conflicting interval; other positions remain unchanged.
internal sealed class PassOrder
{
    // Frame work uses the positions between consecutive persistent positions.
    internal const long PositionGap = 1L << 32;
    private readonly Dictionary<int, long> _positions = [];
    private readonly SortedDictionary<long, int> _ordered = [];
    internal readonly HashSet<int> Changed = [];

    internal int Count => _positions.Count;
    internal IEnumerable<int> Nodes => _ordered.Values;
    internal bool Contains(int node) => _positions.ContainsKey(node);
    internal long Position(int node) => _positions[node];

    internal void Add(int node, long position)
    {
        while (_ordered.ContainsKey(position)) position = checked(position + 1);
        _positions.Add(node, position);
        _ordered.Add(position, node);
        Changed.Add(node);
    }

    internal bool Remove(int node)
    {
        if (!_positions.Remove(node, out long position)) return false;
        _ordered.Remove(position);
        Changed.Add(node);
        return true;
    }

    internal void SetOrder(IReadOnlyList<int> nodes)
    {
        long[] positions = nodes.Select(node => _positions[node]).Order().ToArray();
        foreach (long position in positions) _ordered.Remove(position);
        for (int index = 0; index < nodes.Count; index++)
        {
            int node = nodes[index];
            if (_positions[node] != positions[index])
            {
                _positions[node] = positions[index];
                Changed.Add(node);
            }
            _ordered.Add(positions[index], node);
        }
    }

    internal void Repair(Func<int, IReadOnlyList<int>> predecessors, Func<int, string> label,
        IEnumerable<int>? consumers = null)
    {
        while (true)
        {
            int firstNode = -1, lastNode = -1;
            foreach (int consumer in consumers ?? Nodes)
            {
                if (!Contains(consumer)) continue;
                foreach (int predecessor in predecessors(consumer))
                    if (_positions[predecessor] >= _positions[consumer])
                    {
                        firstNode = consumer;
                        lastNode = predecessor;
                        break;
                    }
                if (firstNode >= 0) break;
            }
            if (firstNode < 0) return;
            int[] order = _ordered.Values.ToArray();
            int first = Array.IndexOf(order, firstNode), last = Array.IndexOf(order, lastNode);

            int[] interval = order.AsSpan(first, last - first + 1).ToArray();
            var members = interval.ToHashSet();
            var inputs = interval.ToDictionary(node => node, _ => 0);
            var outputs = interval.ToDictionary(node => node, _ => new List<int>());
            foreach (int consumer in interval)
                foreach (int predecessor in predecessors(consumer))
                    if (members.Contains(predecessor))
                    {
                        inputs[consumer]++;
                        outputs[predecessor].Add(consumer);
                    }

            var ready = new PriorityQueue<int, long>();
            foreach (int node in interval)
                if (inputs[node] == 0) ready.Enqueue(node, _positions[node]);
            int count = 0;
            while (ready.TryDequeue(out int node, out _))
            {
                interval[count++] = node;
                foreach (int consumer in outputs[node])
                    if (--inputs[consumer] == 0) ready.Enqueue(consumer, _positions[consumer]);
            }
            if (count != interval.Length)
            {
                var steps = new List<int>();
                var visited = new Dictionary<int, int>();
                int node = members.First(value => inputs[value] != 0);
                while (!visited.ContainsKey(node))
                {
                    visited.Add(node, steps.Count);
                    steps.Add(node);
                    node = predecessors(node).First(value => members.Contains(value) && inputs[value] != 0);
                }
                throw new InvalidOperationException("The frame dependency graph contains a cycle: " +
                    string.Join(" <- ", steps.Skip(visited[node]).Append(node).Select(label)));
            }
            SetOrder(interval);
        }
    }
}
