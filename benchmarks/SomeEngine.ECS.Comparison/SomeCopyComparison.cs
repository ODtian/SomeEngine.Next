using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

public readonly record struct SomeCopyNode(Entity Self, Entity Next, Entity Outside, int Ordinal, int Payload)
    : IComponent, IEntityReferenceValue<SomeCopyNode>
{
    public static void Remap(ref SomeCopyNode value, in EntityMap entities)
        => value = value with { Self = entities.Map(value.Self), Next = entities.Map(value.Next), Outside = entities.Map(value.Outside) };
}
public readonly record struct SomeCopyItem(Entity Entity, long Value) : IEntityReferenceValue<SomeCopyItem>
{
    public static void Remap(ref SomeCopyItem value, in EntityMap entities) => value = value with { Entity = entities.Map(value.Entity) };
}

internal sealed class SomeCopyComparison : ICopyComparison<Entity, Entity>
{
    private readonly World _world;
    private readonly EntityPrefab? _prefab;
    private readonly ValueIndex<IndexedNumber, int> _index;
    public Entity[] Sources { get; }
    public Entity Outside { get; }
    public Entity Key(Entity entity) => entity;

    internal SomeCopyComparison(World world, Entity[] entities, bool prefab)
    {
        _world = world; Sources = entities;
        Outside = world.Create(new GrowthMarker { Value = -1 });
        Span<SomeCopyItem> items = stackalloc SomeCopyItem[4];
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i], next = entities[(i + 1) % entities.Length];
            world.Set(entity, new SomeCopyNode(entity, next, Outside, i, i * 3));
            world.Set(entity, new IndexedNumber(i % 16, i));
            world.AddBuffer<SomeCopyItem>(entity, 4);
            using var buffer = world.GetBuffer<SomeCopyItem>(entity);
            items[0] = new(entity, i * 4L); items[1] = new(next, i * 4L + 1);
            items[2] = new(Outside, i * 4L + 2); items[3] = new(next, i * 4L + 3);
            buffer.AddRange(items);
        }
        _index = world.Index<IndexedNumber, int>();
        if (prefab) _prefab = new(world, entities);
    }

    public void Copy(Span<Entity> targets, int copies)
    {
        if (_prefab is { } prefab) prefab.Instantiate(_world, targets, copies);
        else for (int i = 0; i < copies; i++) _world.Clone(Sources, targets.Slice(i * Sources.Length, Sources.Length));
    }

    public CopyGraphObservation<Entity> Capture(Entity entity)
    {
        SomeCopyNode node = _world.Read<SomeCopyNode>(entity);
        using var buffer = _world.ReadBuffer<SomeCopyItem>(entity);
        var a = buffer[0]; var b = buffer[1]; var c = buffer[2]; var d = buffer[3];
        return new(new(node.Self, node.Next, node.Outside, node.Ordinal, node.Payload),
            new(a.Entity, a.Value), new(b.Entity, b.Value), new(c.Entity, c.Value), new(d.Entity, d.Value),
            _world.Read<Position>(entity), _world.Read<Velocity>(entity), _world.Read<Health>(entity), buffer.Count,
            (byte)((_world.Has<Team>(entity) ? 1 : 0) | (_world.Has<Sleeping>(entity) ? 2 : 0)));
    }

    public long IncrementCopyAndReadSource(Entity copied, Entity source)
    {
        using (var buffer = _world.GetBuffer<SomeCopyItem>(copied)) buffer[0] = buffer[0] with { Value = buffer[0].Value + 1 };
        using var original = _world.ReadBuffer<SomeCopyItem>(source);
        return original[0].Value;
    }

    public (int Count, long Sum) ReadIndex(int key)
    {
        using var found = _index.Find(key);
        long sum = 0;
        foreach (Entity entity in found) sum += _world.Read<IndexedNumber>(entity).Ordinal;
        return (found.Count, sum);
    }
    public void Destroy(ReadOnlySpan<Entity> entities) => _world.Destroy(entities);
    public void Dispose() => _prefab?.Dispose();
}
