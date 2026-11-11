using System.Runtime.CompilerServices;
namespace SomeEngine.ECS.Comparison;

[Friflo.Engine.ECS.GenericInstanceType("NativeCopyBuffer", typeof(Friflo.Engine.ECS.RawEntity))]
public struct NativeCopyBuffer<TKey> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TKey : struct
{
    internal NativeCopyBufferData<TKey> Data;

    public static void CopyValue(in NativeCopyBuffer<TKey> source, ref NativeCopyBuffer<TKey> target,
        in Friflo.Engine.ECS.CopyContext context) => target = source;
}

internal sealed class NativeCopyBufferData<TKey> where TKey : struct
{
    internal CopyGraphItem<TKey>[] Items = [];
    internal int Count;
    internal void Reserve(int count)
    {
        if (count > Items.Length) Array.Resize(ref Items, Math.Max(count, Math.Max(4, Items.Length * 2)));
    }
}

internal sealed class NativeCopyBufferPool<TKey> where TKey : struct
{
    private NativeCopyBufferData<TKey>[] _free = [];
    private int _count;
    internal NativeCopyBufferData<TKey> Rent(int capacity = 4)
    {
        NativeCopyBufferData<TKey> data;
        if (_count == 0) data = new();
        else { data = _free[--_count]; _free[_count] = null!; }
        data.Reserve(capacity); return data;
    }
    internal NativeCopyBufferData<TKey> Clone(NativeCopyBufferData<TKey> source)
    {
        var data = Rent(source.Count); source.Items.AsSpan(0, source.Count).CopyTo(data.Items); data.Count = source.Count; return data;
    }
    internal void Return(NativeCopyBufferData<TKey> data)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) Array.Clear(data.Items, 0, data.Count);
        data.Count = 0;
        if (data.Items.Length * Unsafe.SizeOf<CopyGraphItem<TKey>>() > 4096) return;
        if (_count == _free.Length) Array.Resize(ref _free, Math.Max(4, _count * 2));
        _free[_count++] = data;
    }
    internal void Clear() { _free = []; _count = 0; }
}

internal interface INativeCopyValues<TEntity, TKey> : IDisposable where TEntity : struct where TKey : struct
{
    TKey Key(TEntity entity);
    TEntity Outside();
    void SetGraph(TEntity entity, CopyGraphNode<TKey> node, NativeCopyBuffer<TKey> buffer, IndexedNumber number);
    void ActivateIndex();
    TEntity Clone(TEntity entity, NativeCopyBufferPool<TKey> buffers);
    void Freeze(ReadOnlySpan<TEntity> entities, NativeCopyBufferPool<TKey> buffers);
    TEntity Instantiate(int ordinal, NativeCopyBufferPool<TKey> buffers);
    ref CopyGraphNode<TKey> Node(TEntity entity);
    NativeCopyBufferData<TKey> Buffer(TEntity entity);
    CopyGraphObservation<TKey> Capture(TEntity entity);
    (int Count, long Sum) ReadIndex(int key);
    void Destroy(TEntity entity);
}

internal sealed class NativeCopyComparison<TEntity, TKey, TValues> : ICopyComparison<TEntity, TKey>
    where TEntity : struct where TKey : struct where TValues : class, INativeCopyValues<TEntity, TKey>
{
    private readonly TValues _values;
    private readonly bool _prefab;
    private readonly NativeCopyBufferPool<TKey> _buffers = new();
    private readonly Dictionary<TKey, TKey> _mapped;
    public TEntity[] Sources { get; }
    public TKey Outside { get; }
    public TKey Key(TEntity entity) => _values.Key(entity);

    internal NativeCopyComparison(TValues values, TEntity[] entities, bool prefab)
    {
        _values = values; Sources = entities; _prefab = prefab; _mapped = new(entities.Length);
        Outside = values.Key(values.Outside());
        for (int i = 0; i < entities.Length; i++)
        {
            TKey self = values.Key(entities[i]), next = values.Key(entities[(i + 1) % entities.Length]);
            var data = _buffers.Rent();
            data.Count = 4;
            data.Items[0] = new(self, i * 4L); data.Items[1] = new(next, i * 4L + 1);
            data.Items[2] = new(Outside, i * 4L + 2); data.Items[3] = new(next, i * 4L + 3);
            values.SetGraph(entities[i], new(self, next, Outside, i, i * 3), new() { Data = data }, new(i % 16, i));
        }
        values.ActivateIndex();
        if (prefab) values.Freeze(entities, _buffers);
    }

    public void Copy(Span<TEntity> targets, int copies)
    {
        for (int copy = 0; copy < copies; copy++)
        {
            Span<TEntity> created = targets.Slice(copy * Sources.Length, Sources.Length);
            _mapped.Clear();
            for (int i = 0; i < Sources.Length; i++)
            {
                created[i] = _prefab ? _values.Instantiate(i, _buffers) : _values.Clone(Sources[i], _buffers);
                _mapped.Add(Key(Sources[i]), Key(created[i]));
            }
            TKey Map(TKey key) => _mapped.TryGetValue(key, out TKey mapped) ? mapped : key;
            for (int i = 0; i < created.Length; i++)
            {
                ref var node = ref _values.Node(created[i]);
                node = node with { Self = Map(node.Self), Next = Map(node.Next), Outside = Map(node.Outside) };
                var buffer = _values.Buffer(created[i]);
                for (int item = 0; item < buffer.Count; item++) buffer.Items[item] = buffer.Items[item] with { Entity = Map(buffer.Items[item].Entity) };
            }
        }
    }

    public CopyGraphObservation<TKey> Capture(TEntity entity) => _values.Capture(entity);
    public long IncrementCopyAndReadSource(TEntity copied, TEntity source)
    { var data = _values.Buffer(copied); data.Items[0] = data.Items[0] with { Value = data.Items[0].Value + 1 }; return _values.Buffer(source).Items[0].Value; }
    public (int Count, long Sum) ReadIndex(int key) => _values.ReadIndex(key);
    public void Destroy(ReadOnlySpan<TEntity> entities)
    {
        foreach (TEntity entity in entities) { _buffers.Return(_values.Buffer(entity)); _values.Destroy(entity); }
    }
    public void Dispose()
    {
        foreach (TEntity entity in Sources) _buffers.Return(_values.Buffer(entity));
        _values.Dispose(); _buffers.Clear(); _mapped.Clear();
    }
}
