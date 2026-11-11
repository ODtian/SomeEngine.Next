namespace SomeEngine.ECS.Comparison;

internal sealed class DefaultCopyValues(DefaultEcs.World world) : INativeCopyValues<DefaultEcs.Entity, DefaultEcs.Entity>
{
    private DefaultEcs.EntityMultiMap<ReferenceIndexedNumber>? _index;
    private DefaultEcs.World? _templates;
    private DefaultEcs.Entity[] _templateEntities = [];
    private NativeCopyBufferPool<DefaultEcs.Entity>? _templateBuffers;
    public DefaultEcs.Entity Key(DefaultEcs.Entity entity) => entity;
    public DefaultEcs.Entity Outside() { var entity = world.CreateEntity(); entity.Set(new GrowthMarker { Value = -1 }); return entity; }
    public void SetGraph(DefaultEcs.Entity entity, CopyGraphNode<DefaultEcs.Entity> node, NativeCopyBuffer<DefaultEcs.Entity> buffer, IndexedNumber number)
    { entity.Set(node); entity.Set(buffer); entity.Set(ReferenceIndexedNumber.Project(number)); }
    public void ActivateIndex() => _index = world.GetEntities().With<ReferenceIndexedNumber>().AsMultiMap<ReferenceIndexedNumber>();
    public ref CopyGraphNode<DefaultEcs.Entity> Node(DefaultEcs.Entity entity) => ref entity.Get<CopyGraphNode<DefaultEcs.Entity>>();
    public NativeCopyBufferData<DefaultEcs.Entity> Buffer(DefaultEcs.Entity entity) => entity.Get<NativeCopyBuffer<DefaultEcs.Entity>>().Data;

    private DefaultEcs.Entity CopyTo(DefaultEcs.Entity entity, DefaultEcs.World target, NativeCopyBufferPool<DefaultEcs.Entity> buffers)
    {
        var copied = entity.CopyTo(target);
        copied.Get<NativeCopyBuffer<DefaultEcs.Entity>>().Data = buffers.Clone(Buffer(entity));
        return copied;
    }
    public DefaultEcs.Entity Clone(DefaultEcs.Entity entity, NativeCopyBufferPool<DefaultEcs.Entity> buffers) => CopyTo(entity, world, buffers);
    public void Freeze(ReadOnlySpan<DefaultEcs.Entity> entities, NativeCopyBufferPool<DefaultEcs.Entity> buffers)
    {
        _templates = new(); _templateEntities = new DefaultEcs.Entity[entities.Length]; _templateBuffers = buffers;
        for (int i = 0; i < entities.Length; i++) _templateEntities[i] = CopyTo(entities[i], _templates, buffers);
    }
    public DefaultEcs.Entity Instantiate(int ordinal, NativeCopyBufferPool<DefaultEcs.Entity> buffers) => CopyTo(_templateEntities[ordinal], world, buffers);

    public CopyGraphObservation<DefaultEcs.Entity> Capture(DefaultEcs.Entity entity)
    {
        var data = Buffer(entity); var items = data.Items;
        return new(Node(entity), items[0], items[1], items[2], items[3], entity.Get<Position>(), entity.Get<Velocity>(), entity.Get<Health>(), data.Count,
            (byte)((entity.Has<Team>() ? 1 : 0) | (entity.Has<Sleeping>() ? 2 : 0)));
    }
    public (int Count, long Sum) ReadIndex(int key)
    {
        int count = 0; long sum = 0;
        if (_index!.TryGetEntities(new(key, 0), out var entities))
            foreach (var entity in entities) { count++; sum += entity.Get<ReferenceIndexedNumber>().Ordinal; }
        return (count, sum);
    }
    public void Destroy(DefaultEcs.Entity entity) => entity.Dispose();
    public void Dispose()
    {
        foreach (var entity in _templateEntities) _templateBuffers!.Return(Buffer(entity));
        _templateEntities = []; _templates?.Dispose(); _templates = null; _templateBuffers = null;
        _index?.Dispose();
    }
}
