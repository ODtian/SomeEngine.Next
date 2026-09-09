namespace SomeEngine.RenderGraph;

// Retains execution positions so rescheduling one component leaves unrelated
// components in place. Dependency edges belong to FrameExecutor's existing graph.
internal sealed class PassOrder
{
    private readonly Dictionary<int, long> _positions = [];
    private readonly SortedDictionary<long, int> _ordered = [];
    internal readonly HashSet<int> Changed = [];

    internal int Count => _positions.Count;
    internal IEnumerable<int> Nodes => _ordered.Values;

    internal void Add(int node, long position)
    {
        while (_ordered.ContainsKey(position)) position = checked(position + 1);
        _positions.Add(node, position);
        _ordered.Add(position, node);
        Changed.Add(node);
    }

    internal void Remove(int node)
    {
        if (!_positions.Remove(node, out long position)) return;
        _ordered.Remove(position);
        Changed.Add(node);
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
}
