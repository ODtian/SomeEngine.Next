using Arch.Core;

namespace SomeEngine.ECS.Comparison;

internal sealed class ArchCopyValues(Arch.Core.World world, Arch.Core.Entity[] sources) : INativeCopyValues<Arch.Core.Entity, Arch.Core.Entity>
{
    private ReferenceEntityIndex<Arch.Core.Entity>? _index;
    private Arch.Core.World? _templates;
    private Arch.Core.Entity[] _templateEntities = [];
    private NativeCopyBufferPool<Arch.Core.Entity>? _templateBuffers;
    public Arch.Core.Entity Key(Arch.Core.Entity entity) => entity;
    public Arch.Core.Entity Outside() => world.Create(new GrowthMarker { Value = -1 });
    public void SetGraph(Arch.Core.Entity entity, CopyGraphNode<Arch.Core.Entity> node, NativeCopyBuffer<Arch.Core.Entity> buffer, IndexedNumber number)
    {
        if (world.Has<CopyGraphNode<Arch.Core.Entity>>(entity))
        {
            world.Set(entity, node);
            world.Add(entity, buffer, ReferenceIndexedNumber.Project(number));
        }
        else world.Add(entity, node, buffer, ReferenceIndexedNumber.Project(number));
    }
    public void ActivateIndex()
    {
        _index = new(sources.Length + 1_001, entity => entity.Id);
        foreach (var entity in sources) _index.Set(entity, world.Get<ReferenceIndexedNumber>(entity).Key);
    }
    public ref CopyGraphNode<Arch.Core.Entity> Node(Arch.Core.Entity entity) => ref world.Get<CopyGraphNode<Arch.Core.Entity>>(entity);
    public NativeCopyBufferData<Arch.Core.Entity> Buffer(Arch.Core.Entity entity) => world.Get<NativeCopyBuffer<Arch.Core.Entity>>(entity).Data;

    private Arch.Core.Entity CreateFrom(Arch.Core.World target, Arch.Core.World from, Arch.Core.Entity entity, NativeCopyBufferData<Arch.Core.Entity> data)
    {
        var p = from.Get<Position>(entity); var v = from.Get<Velocity>(entity); var h = from.Get<Health>(entity);
        var node = from.Get<CopyGraphNode<Arch.Core.Entity>>(entity); var number = from.Get<ReferenceIndexedNumber>(entity);
        var buffer = new NativeCopyBuffer<Arch.Core.Entity> { Data = data };
        var copied = from.Has<Sleeping>(entity) ? target.Create(p, v, h, new Team(), new Sleeping(), node, number, buffer)
            : from.Has<Team>(entity) ? target.Create(p, v, h, new Team(), node, number, buffer)
            : target.Create(p, v, h, node, number, buffer);
        if (ReferenceEquals(target, world)) _index!.Set(copied, number.Key);
        return copied;
    }
    public Arch.Core.Entity Clone(Arch.Core.Entity entity, NativeCopyBufferPool<Arch.Core.Entity> buffers)
        => CreateFrom(world, world, entity, buffers.Clone(Buffer(entity)));
    public void Freeze(ReadOnlySpan<Arch.Core.Entity> entities, NativeCopyBufferPool<Arch.Core.Entity> buffers)
    {
        _templates = Arch.Core.World.Create(); _templateEntities = new Arch.Core.Entity[entities.Length]; _templateBuffers = buffers;
        for (int i = 0; i < entities.Length; i++) _templateEntities[i] = CreateFrom(_templates, world, entities[i], buffers.Clone(Buffer(entities[i])));
    }
    public Arch.Core.Entity Instantiate(int ordinal, NativeCopyBufferPool<Arch.Core.Entity> buffers)
    {
        var entity = _templateEntities[ordinal];
        var data = _templates!.Get<NativeCopyBuffer<Arch.Core.Entity>>(entity).Data;
        return CreateFrom(world, _templates, entity, buffers.Clone(data));
    }
    public CopyGraphObservation<Arch.Core.Entity> Capture(Arch.Core.Entity entity)
    {
        var data = Buffer(entity); var items = data.Items;
        return new(Node(entity), items[0], items[1], items[2], items[3], world.Get<Position>(entity), world.Get<Velocity>(entity), world.Get<Health>(entity), data.Count,
            (byte)((world.Has<Team>(entity) ? 1 : 0) | (world.Has<Sleeping>(entity) ? 2 : 0)));
    }
    public (int Count, long Sum) ReadIndex(int key)
    {
        var found = _index!.Find(key); long sum = 0;
        foreach (var entity in found) sum += world.Get<ReferenceIndexedNumber>(entity).Ordinal;
        return (found.Length, sum);
    }
    public void Destroy(Arch.Core.Entity entity) { _index!.Remove(entity); world.Destroy(entity); }
    public void Dispose()
    {
        foreach (var entity in _templateEntities) _templateBuffers!.Return(_templates!.Get<NativeCopyBuffer<Arch.Core.Entity>>(entity).Data);
        _templateEntities = []; if (_templates is not null) Arch.Core.World.Destroy(_templates);
        _templates = null; _templateBuffers = null; _index = null;
    }
}
