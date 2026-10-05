namespace SomeEngine.RenderGraph;

internal sealed class FramePassOrders : IReadOnlyList<ExplicitPassOrder>
{
    private readonly List<ExplicitPassOrder> _orders = [];
    private readonly Dictionary<GraphIdentity, List<int>> _incoming = [];
    private readonly Dictionary<GraphIdentity, List<int>> _outgoing = [];

    public int Count => _orders.Count;
    public ExplicitPassOrder this[int index] => _orders[index];

    internal void Add(ExplicitPassOrder order)
    {
        int index = _orders.Count;
        _orders.Add(order);
        AddIndex(_incoming, order.Consumer, index);
        AddIndex(_outgoing, order.Predecessor, index);
    }

    internal IEnumerable<ExplicitPassOrder> Incident(GraphIdentity pass)
    {
        if (_incoming.TryGetValue(pass, out List<int>? incoming))
        {
            int count = incoming.Count;
            for (int index = 0; index < count; index++) yield return _orders[incoming[index]];
        }
        if (_outgoing.TryGetValue(pass, out List<int>? outgoing))
        {
            int count = outgoing.Count;
            for (int index = 0; index < count; index++) yield return _orders[outgoing[index]];
        }
    }

    internal void Clear()
    {
        _orders.Clear();
        _incoming.Clear();
        _outgoing.Clear();
    }

    private static void AddIndex(Dictionary<GraphIdentity, List<int>> index, GraphIdentity pass, int row)
    {
        if (!index.TryGetValue(pass, out List<int>? rows)) index.Add(pass, rows = []);
        rows.Add(row);
    }

    public List<ExplicitPassOrder>.Enumerator GetEnumerator() => _orders.GetEnumerator();
    IEnumerator<ExplicitPassOrder> IEnumerable<ExplicitPassOrder>.GetEnumerator() => _orders.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _orders.GetEnumerator();
}
