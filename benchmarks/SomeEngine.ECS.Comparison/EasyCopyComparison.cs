using EasyEcs;

namespace SomeEngine.ECS.Comparison;

public struct EasyCopyNode(EntityId self, EntityId next, EntityId outside, int ordinal, int payload) : EasyEcs.IComponent
{
    public EntityId Self = self, Next = next, Outside = outside;
    public int Ordinal = ordinal, Payload = payload;
}
public struct EasyCopyItem(EntityId entity, long value) : EasyEcs.IComponent
{
    public EntityId Entity = entity;
    public long Value = value;
}

internal sealed class EasyCopyComparison : ICopyComparison<EasyEcs.Entity, EntityId>
{
    private readonly EasyEcs.World _world;
    private readonly EasyEcs.ValueIndex<IndexedNumber, int> _index;
    private EasyEcs.Prefab? _prefab;
    private readonly Dictionary<EntityId, EntityId> _mapped;
    public EasyEcs.Entity[] Sources { get; }
    public EntityId Outside { get; }
    public EntityId Key(EasyEcs.Entity entity) => entity.Id;

    internal EasyCopyComparison(EasyEcs.World world, EasyEcs.Entity[] entities, bool prefab)
    {
        _world = world; Sources = entities; _mapped = new(entities.Length);
        Outside = world.CreateEntity(new GrowthMarker { Value = -1 }).Id;
        Span<EasyCopyItem> items = stackalloc EasyCopyItem[4];
        for (int i = 0; i < entities.Length; i++)
        {
            var entity = entities[i]; EntityId self = entity.Id, next = entities[(i + 1) % entities.Length].Id;
            entity.Set(new EasyCopyNode(self, next, Outside, i, i * 3));
            entity.Set(new IndexedNumber(i % 16, i));
            entity.Set(new EasyEcs.DynamicBuffer<EasyCopyItem>());
            ref var buffer = ref entity.Get<EasyEcs.DynamicBuffer<EasyCopyItem>>();
            items[0] = new(self, i * 4L); items[1] = new(next, i * 4L + 1);
            items[2] = new(Outside, i * 4L + 2); items[3] = new(next, i * 4L + 3);
            buffer.AddRange(items);
        }
        _index = world.Index<IndexedNumber, int>();
        if (prefab)
        {
            _prefab = new(); var members = new EasyEcs.Prefab[entities.Length]; members[0] = _prefab;
            for (int i = 1; i < members.Length; i++) members[i] = _prefab.AddMember();
            for (int i = 0; i < members.Length; i++)
            {
                var entity = entities[i]; EntityId self = members[i].Id, next = members[(i + 1) % members.Length].Id;
                members[i].Set(entity.Read<Position>(), entity.Read<Velocity>(), entity.Read<Health>(),
                    new IndexedNumber(i % 16, i), new EasyCopyNode(self, next, default, i, i * 3));
                if (entity.Has<Team>()) members[i].Set(new Team());
                if (entity.Has<Sleeping>()) members[i].Set(new Sleeping());
                members[i].SetBuffer<EasyCopyItem>([new(self, i * 4L), new(next, i * 4L + 1), new(default, i * 4L + 2), new(next, i * 4L + 3)]);
            }
        }
    }

    public void Copy(Span<EasyEcs.Entity> targets, int copies)
    {
        for (int copy = 0; copy < copies; copy++)
        {
            Span<EasyEcs.Entity> created = targets.Slice(copy * Sources.Length, Sources.Length);
            if (_prefab is { } prefab)
            {
                var entity = _world.Instantiate(prefab);
                for (int i = 0; i < created.Length; i++)
                {
                    created[i] = entity;
                    ref var node = ref entity.Get<EasyCopyNode>(); EntityId next = node.Next;
                    node.Outside = Outside;
                    ref var buffer = ref entity.Get<EasyEcs.DynamicBuffer<EasyCopyItem>>();
                    buffer.AsSpan()[2].Entity = Outside;
                    if (i + 1 < created.Length) entity = _world.GetEntity(next);
                }
            }
            else
            {
                _mapped.Clear();
                for (int i = 0; i < created.Length; i++) { created[i] = Sources[i].Clone(); _mapped.Add(Sources[i].Id, created[i].Id); }
                EntityId Map(EntityId id) => _mapped.TryGetValue(id, out EntityId mapped) ? mapped : id;
                foreach (var entity in created)
                {
                    ref var node = ref entity.Get<EasyCopyNode>();
                    node.Self = Map(node.Self); node.Next = Map(node.Next); node.Outside = Map(node.Outside);
                    ref var buffer = ref entity.Get<EasyEcs.DynamicBuffer<EasyCopyItem>>();
                    foreach (ref var item in buffer.AsSpan()) item.Entity = Map(item.Entity);
                }
            }
        }
    }

    public CopyGraphObservation<EntityId> Capture(EasyEcs.Entity entity)
    {
        ref readonly var buffer = ref entity.Read<EasyEcs.DynamicBuffer<EasyCopyItem>>(); var items = buffer.AsReadOnlySpan();
        var node = entity.Read<EasyCopyNode>();
        return new(new(node.Self, node.Next, node.Outside, node.Ordinal, node.Payload),
            new(items[0].Entity, items[0].Value), new(items[1].Entity, items[1].Value), new(items[2].Entity, items[2].Value), new(items[3].Entity, items[3].Value),
            entity.Read<Position>(), entity.Read<Velocity>(), entity.Read<Health>(), buffer.Count,
            (byte)((entity.Has<Team>() ? 1 : 0) | (entity.Has<Sleeping>() ? 2 : 0)));
    }
    public long IncrementCopyAndReadSource(EasyEcs.Entity copied, EasyEcs.Entity source)
    {
        ref var buffer = ref copied.Get<EasyEcs.DynamicBuffer<EasyCopyItem>>(); buffer.AsSpan()[0].Value++;
        return source.Read<EasyEcs.DynamicBuffer<EasyCopyItem>>().AsReadOnlySpan()[0].Value;
    }
    public (int Count, long Sum) ReadIndex(int key)
    {
        int count = 0; long sum = 0;
        foreach (var entity in _index.Find(key)) { count++; sum += entity.Read<IndexedNumber>().Ordinal; }
        return (count, sum);
    }
    public void Destroy(ReadOnlySpan<EasyEcs.Entity> entities) { foreach (var entity in entities) entity.Destroy(); }
    public void Dispose() { _prefab = null; _mapped.Clear(); }
}
