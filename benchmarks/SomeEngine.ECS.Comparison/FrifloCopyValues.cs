using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

internal sealed class FrifloCopyValues(EntityStore world) : INativeCopyValues<Friflo.Engine.ECS.Entity, RawEntity>
{
    private ComponentIndex<ReferenceIndexedNumber, int> _index;
    private EntityStore? _templates;
    private Friflo.Engine.ECS.Entity[] _templateEntities = [];
    private NativeCopyBufferPool<RawEntity>? _templateBuffers;
    public RawEntity Key(Friflo.Engine.ECS.Entity entity) => entity.RawEntity;
    public Friflo.Engine.ECS.Entity Outside() => world.CreateEntity(new GrowthMarker { Value = -1 });
    public void SetGraph(Friflo.Engine.ECS.Entity entity, CopyGraphNode<RawEntity> node, NativeCopyBuffer<RawEntity> buffer, IndexedNumber number)
    { entity.AddComponent(node); entity.AddComponent(buffer); entity.AddComponent(ReferenceIndexedNumber.Project(number)); }
    public void ActivateIndex() => _index = world.ComponentIndex<ReferenceIndexedNumber, int>();
    public ref CopyGraphNode<RawEntity> Node(Friflo.Engine.ECS.Entity entity) => ref entity.GetComponent<CopyGraphNode<RawEntity>>();
    public NativeCopyBufferData<RawEntity> Buffer(Friflo.Engine.ECS.Entity entity) => entity.GetComponent<NativeCopyBuffer<RawEntity>>().Data;

    public Friflo.Engine.ECS.Entity Clone(Friflo.Engine.ECS.Entity entity, NativeCopyBufferPool<RawEntity> buffers)
    {
        var copy = entity.CloneEntity();
        copy.GetComponent<NativeCopyBuffer<RawEntity>>().Data = buffers.Clone(Buffer(entity));
        return copy;
    }

    private static Friflo.Engine.ECS.Entity CreateFrom(EntityStore target, Friflo.Engine.ECS.Entity entity, NativeCopyBufferData<RawEntity> data)
    {
        var p = entity.GetComponent<Position>(); var v = entity.GetComponent<Velocity>(); var h = entity.GetComponent<Health>();
        var node = entity.GetComponent<CopyGraphNode<RawEntity>>(); var number = entity.GetComponent<ReferenceIndexedNumber>();
        var buffer = new NativeCopyBuffer<RawEntity> { Data = data };
        return entity.HasComponent<Sleeping>() ? target.CreateEntity(p, v, h, new Team(), new Sleeping(), node, number, buffer)
            : entity.HasComponent<Team>() ? target.CreateEntity(p, v, h, new Team(), node, number, buffer)
            : target.CreateEntity(p, v, h, node, number, buffer);
    }

    public void Freeze(ReadOnlySpan<Friflo.Engine.ECS.Entity> entities, NativeCopyBufferPool<RawEntity> buffers)
    {
        _templates = new(); _templateEntities = new Friflo.Engine.ECS.Entity[entities.Length]; _templateBuffers = buffers;
        for (int i = 0; i < entities.Length; i++) _templateEntities[i] = CreateFrom(_templates, entities[i], buffers.Clone(Buffer(entities[i])));
    }
    public Friflo.Engine.ECS.Entity Instantiate(int ordinal, NativeCopyBufferPool<RawEntity> buffers)
    { var entity = _templateEntities[ordinal]; return CreateFrom(world, entity, buffers.Clone(Buffer(entity))); }

    public CopyGraphObservation<RawEntity> Capture(Friflo.Engine.ECS.Entity entity)
    {
        var data = Buffer(entity); var items = data.Items;
        return new(Node(entity), items[0], items[1], items[2], items[3], entity.GetComponent<Position>(), entity.GetComponent<Velocity>(),
            entity.GetComponent<Health>(), data.Count, (byte)((entity.HasComponent<Team>() ? 1 : 0) | (entity.HasComponent<Sleeping>() ? 2 : 0)));
    }
    public (int Count, long Sum) ReadIndex(int key)
    {
        int count = 0; long sum = 0;
        foreach (var entity in _index[key]) { count++; sum += entity.GetComponent<ReferenceIndexedNumber>().Ordinal; }
        return (count, sum);
    }
    public void Destroy(Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
    public void Dispose()
    {
        foreach (var entity in _templateEntities) _templateBuffers!.Return(Buffer(entity));
        _templateEntities = []; _templates = null; _templateBuffers = null;
    }
}
