namespace SomeEngine.RenderGraph;

// Retains execution positions. Repairing a dependency reorders only the nodes
// participating in its conflicting interval; other positions remain unchanged.
internal sealed class PassOrder
{
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
}
