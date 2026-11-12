using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

internal readonly record struct EntityMapEntry(Entity Target, int Generation, int Epoch);
internal sealed class EntityMapData
{
    internal EntityMapEntry[] Entries = [];
    internal readonly Dictionary<Entity, Entity> Index = new();
    internal int Epoch;
}

public interface IEntityMapper
{
    Entity Map(Entity entity);
}

public readonly unsafe ref struct EntityMap
{
    private readonly ReadOnlySpan<Entity> _sources, _targets;
    private readonly Dictionary<Entity, Entity>? _indexed;
    private readonly ref byte _external;
    private readonly delegate*<ref byte, Entity, Entity> _mapExternal;
    private readonly ReadOnlySpan<EntityMapEntry> _dense;
    private readonly int _first, _epoch;

    // Prefab references use the fixed prefix (-index - 1, 1).
    internal EntityMap(ReadOnlySpan<Entity> targets)
        : this(default, targets, null, -1, ref Unsafe.NullRef<byte>(), null) { }

    internal EntityMap(ReadOnlySpan<Entity> sources, ReadOnlySpan<Entity> targets, EntityMapData? data = null)
        : this(sources, targets, data, 0, ref Unsafe.NullRef<byte>(), null) { }

    private EntityMap(ReadOnlySpan<Entity> sources, ReadOnlySpan<Entity> targets, EntityMapData? data,
        int first, ref byte external, delegate*<ref byte, Entity, Entity> mapExternal)
    {
        _sources = sources; _targets = targets; _external = ref external; _mapExternal = mapExternal; _indexed = null;
        _dense = default; _first = first; _epoch = 0;
        if (sources.Length > 8)
        {
            data ??= new();
            int lowest = sources[0].Id, last = lowest;
            foreach (Entity source in sources) { lowest = Math.Min(lowest, source.Id); last = Math.Max(last, source.Id); }
            int width = last - lowest + 1;
            if (width <= sources.Length * 4)
            {
                if (width > data.Entries.Length) Array.Resize(ref data.Entries, width);
                int epoch = ++data.Epoch;
                for (int i = 0; i < sources.Length; i++) data.Entries[sources[i].Id - lowest] = new(targets[i], sources[i].Generation, epoch);
                _dense = data.Entries.AsSpan(0, width); _first = lowest; _epoch = epoch;
            }
            else
            {
                _indexed = data.Index;
                _indexed.Clear(); _indexed.EnsureCapacity(sources.Length);
                for (int i = 0; i < sources.Length; i++) _indexed.Add(sources[i], targets[i]);
            }
        }
    }

    public static EntityMap Create<TMapper>(ReadOnlySpan<Entity> targets, ref TMapper mapper)
        where TMapper : struct, IEntityMapper
        => new(default, targets, null, -1, ref Unsafe.As<TMapper, byte>(ref mapper), &MapExternal<TMapper>);

    public static EntityMap Create<TMapper>(ref TMapper mapper) where TMapper : struct, IEntityMapper
        => Create(ReadOnlySpan<Entity>.Empty, ref mapper);

    internal static EntityMap Create<TMapper>(ReadOnlySpan<Entity> sources, ReadOnlySpan<Entity> targets,
        ref TMapper mapper, EntityMapData? data) where TMapper : struct, IEntityMapper
        => new(sources, targets, data, 0, ref Unsafe.As<TMapper, byte>(ref mapper), &MapExternal<TMapper>);

    private static Entity MapExternal<TMapper>(ref byte mapper, Entity entity) where TMapper : struct, IEntityMapper
        => Unsafe.As<byte, TMapper>(ref mapper).Map(entity);

    public Entity Map(Entity entity)
    {
        if (entity == Entity.Null) return entity;
        if (!_dense.IsEmpty)
        {
            int index = entity.Id - _first;
            if ((uint)index < (uint)_dense.Length)
            {
                ref readonly EntityMapEntry entry = ref _dense[index];
                if (entry.Epoch == _epoch && entry.Generation == entity.Generation) return entry.Target;
            }
        }
        else if (_first == -1)
        {
            int index = -1 - entity.Id;
            if ((uint)index < (uint)_targets.Length && entity.Generation == 1) return _targets[index];
        }
        else if (_indexed is { } indexed)
        {
            if (indexed.TryGetValue(entity, out Entity target)) return target;
        }
        else
            for (int i = 0; i < _sources.Length; i++)
                if (_sources[i] == entity) return _targets[i];
        return _mapExternal != null ? _mapExternal(ref _external, entity) : entity;
    }
}

public interface IEntityReferenceValue<T> where T : struct
{
    static abstract void Remap(ref T value, in EntityMap entities);
}

public interface IEntityFieldWriter<T> where T : struct
{
    static abstract void Set(ref T value, int field, Entity entity);
}
