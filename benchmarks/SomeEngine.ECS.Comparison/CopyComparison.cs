namespace SomeEngine.ECS.Comparison;

internal interface ICopyScenario : IDisposable
{
    void Cycle();
    void Grow();
    bool Check();
}

[Friflo.Engine.ECS.GenericInstanceType("CopyGraphNode", typeof(Friflo.Engine.ECS.RawEntity))]
public struct CopyGraphNode<TKey>(TKey self, TKey next, TKey outside, int ordinal, int payload)
    : Friflo.Engine.ECS.IComponent where TKey : struct
{
    public TKey Self = self, Next = next, Outside = outside;
    public int Ordinal = ordinal, Payload = payload;
}
public struct CopyGraphItem<TKey>(TKey entity, long value) where TKey : struct
{
    public TKey Entity = entity;
    public long Value = value;
}

internal readonly record struct CopyGraphObservation<TKey>(CopyGraphNode<TKey> Node,
    CopyGraphItem<TKey> A, CopyGraphItem<TKey> B, CopyGraphItem<TKey> C, CopyGraphItem<TKey> D,
    Position Position, Velocity Velocity, Health Health, int Count, byte Layout) where TKey : struct;

internal interface ICopyComparison<TEntity, TKey> : IDisposable where TEntity : struct where TKey : struct
{
    TEntity[] Sources { get; }
    TKey Outside { get; }
    TKey Key(TEntity entity);
    void Copy(Span<TEntity> targets, int copies);
    CopyGraphObservation<TKey> Capture(TEntity entity);
    long IncrementCopyAndReadSource(TEntity copied, TEntity source);
    (int Count, long Sum) ReadIndex(int key);
    void Destroy(ReadOnlySpan<TEntity> entities);
}

internal sealed class CopyScenario<TEntity, TKey, TApi> : ICopyScenario
    where TEntity : struct where TKey : struct where TApi : class, ICopyComparison<TEntity, TKey>
{
    private readonly TApi _api;
    private readonly TEntity[] _copies;
    private bool _valid = true;
    internal CopyScenario(TApi api) { _api = api; _copies = new TEntity[api.Sources.Length * 2]; }

    public void Cycle() => Run(1);
    public void Grow() => Run(2);
    private void Run(int copies)
    {
        int count = _api.Sources.Length;
        Span<TEntity> targets = _copies.AsSpan(0, count * copies);
        _api.Copy(targets, copies);
        bool valid = true;
        var equal = EqualityComparer<TKey>.Default;
        for (int copy = 0; copy < copies; copy++)
        for (int i = 0; i < count; i++)
        {
            TEntity entity = targets[copy * count + i];
            TKey self = _api.Key(entity), next = _api.Key(targets[copy * count + (i + 1) % count]);
            var observed = _api.Capture(entity);
            CopyGraphNode<TKey> node = observed.Node;
            valid &= equal.Equals(node.Self, self) && equal.Equals(node.Next, next) && equal.Equals(node.Outside, _api.Outside)
                && node.Ordinal == i && node.Payload == i * 3
                && observed.Count == 4 && equal.Equals(observed.A.Entity, self) && equal.Equals(observed.B.Entity, next)
                && equal.Equals(observed.C.Entity, _api.Outside) && equal.Equals(observed.D.Entity, next)
                && observed.A.Value == i * 4L && observed.B.Value == i * 4L + 1
                && observed.C.Value == i * 4L + 2 && observed.D.Value == i * 4L + 3
                && observed.Position.X == i && observed.Position.Y == 0 && observed.Velocity.X == 1 && observed.Velocity.Y == 2
                && observed.Health.Value == 100 && observed.Health.Max == 100
                && observed.Layout == (i % 4 == 0 ? 3 : i % 2 == 0 ? 1 : 0)
                && _api.IncrementCopyAndReadSource(entity, _api.Sources[i]) == i * 4L;
        }
        var indexed = _api.ReadIndex(7);
        long expected = 0;
        for (int i = 7; i < count; i += 16) expected += i;
        valid &= indexed.Count == count / 16 * (copies + 1) && indexed.Sum == expected * (copies + 1);
        _api.Destroy(targets);
        _valid &= valid;
    }

    public bool Check() => _valid;
    public void Dispose() { _api.Dispose(); Array.Clear(_copies); }
}
