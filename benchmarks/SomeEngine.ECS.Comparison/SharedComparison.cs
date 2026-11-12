using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public readonly record struct SharedNumber(int Value) : ISharedComponent;
public readonly record struct SharedNumberId(int Value) : EasyEcs.IComponent, EasyEcs.IIndexed<int>,
    Friflo.Engine.ECS.IComponent, Friflo.Engine.ECS.IIndexedComponent<int>
{
    public int Key => Value;
    public int GetIndexedValue() => Value;
}

internal sealed class ReferenceSharedValues<T> where T : struct
{
    private struct Link { internal uint Hash; internal int Next; }
    internal T[] Values = new T[16];
    internal int Count;
    private Link[] _links = new Link[16];
    private int[] _buckets = new int[16];
    internal ReferenceSharedValues() => Intern(default);
    internal int Intern(in T value)
    {
        uint hash = (uint)EqualityComparer<T>.Default.GetHashCode(value);
        int bucket = (int)(hash & (uint)(_buckets.Length - 1));
        for (int next = _buckets[bucket]; next != 0; next = _links[next - 1].Next)
        {
            int id = next - 1;
            if (_links[id].Hash == hash && EqualityComparer<T>.Default.Equals(Values[id], value)) return id;
        }
        if (Count == Values.Length) { Array.Resize(ref Values, Values.Length * 2); Array.Resize(ref _links, Values.Length); }
        if ((Count + 1) * 4 > _buckets.Length * 3)
        {
            var buckets = new int[_buckets.Length * 2];
            for (int i = 0; i < Count; i++)
            {
                int position = (int)(_links[i].Hash & (uint)(buckets.Length - 1));
                _links[i].Next = buckets[position];
                buckets[position] = i + 1;
            }
            _buckets = buckets;
            bucket = (int)(hash & (uint)(buckets.Length - 1));
        }
        int added = Count++;
        Values[added] = value;
        _links[added] = new() { Hash = hash, Next = _buckets[bucket] };
        _buckets[bucket] = added + 1;
        return added;
    }
}

internal sealed class ArchSharedGroups(int capacity)
{
    private struct Members { internal Arch.Core.Entity[]? Items; internal int Count; }
    private Members[] _groups = new Members[16];
    private int[] _groupsByEntity = new int[capacity];
    private int[] _places = new int[capacity];
    internal void Set(Arch.Core.Entity entity, int group)
    {
        int id = entity.Id;
        if (id >= _places.Length)
        {
            int capacity = Math.Max(id + 1, _places.Length * 2);
            Array.Resize(ref _places, capacity);
            Array.Resize(ref _groupsByEntity, capacity);
        }
        int old = _groupsByEntity[id] - 1;
        if (old == group) return;
        if (old >= 0)
        {
            ref Members previous = ref _groups[old];
            int last = --previous.Count;
            int place = _places[id];
            if (place != last)
            {
                var moved = previous.Items![last];
                previous.Items[place] = moved;
                _places[moved.Id] = place;
            }
        }
        if (group >= _groups.Length) Array.Resize(ref _groups, Math.Max(group + 1, _groups.Length * 2));
        ref Members target = ref _groups[group];
        if (target.Items is null) target.Items = new Arch.Core.Entity[4];
        else if (target.Count == target.Items.Length) Array.Resize(ref target.Items, target.Count * 2);
        int next = target.Count++;
        target.Items[next] = entity;
        _groupsByEntity[id] = group + 1;
        _places[id] = next;
    }
    internal void Remove(Arch.Core.Entity entity)
    {
        int id = entity.Id;
        ref Members group = ref _groups[_groupsByEntity[id] - 1];
        int last = --group.Count;
        int place = _places[id];
        if (place != last)
        {
            var moved = group.Items![last];
            group.Items[place] = moved;
            _places[moved.Id] = place;
        }
        _groupsByEntity[id] = 0;
    }
    internal int Count(int group) => (uint)group < (uint)_groups.Length ? _groups[group].Count : 0;
}

internal struct CountEasyShared : EasyEcs.IForEachBatch<SharedNumberId>
{
    public static int Writes => 0;
    internal int Count;
    public void Execute(EasyEcs.EntitySpan entities, Span<SharedNumberId> values) => Count += values.Length;
}

internal sealed partial class SomeEngineWorld
{
    private SharedId<SharedNumber>[]? _sharedIds;
    private Query[]? _sharedQueries;
    private void PrepareShared(string workload)
    {
        if (_sharedIds is not null) return;
        SharedGroupCount = workload.EndsWith("unique") ? Count : 16;
        SharedChanges = workload.Contains("change");
        _sharedIds = new SharedId<SharedNumber>[SharedGroupCount];
        for (int i = 0; i < SharedGroupCount; i++) _sharedIds[i] = _world.InternShared(new SharedNumber(i));
        if (!_world.Has<SharedNumber>(_entities[0]))
            for (int i = 0; i < Count; i++) _world.SetShared(_entities[i], _sharedIds[i % SharedGroupCount]);
        _sharedQueries = new Query[8];
        for (int i = 0; i < 8; i++)
            _sharedQueries[i] = _world.Query(new QueryDescription().WithShared(_sharedIds[SharedGroupCount / 2 - 4 + i]));
    }
    internal override void SharedCycle()
    {
        if (SharedChanges) SharedShift ^= 1;
        for (int i = 0; i < Count; i += 4)
        {
            var value = new SharedNumber((i + SharedShift) % SharedGroupCount);
            if (_world.ReadShared<SharedNumber>(_entities[i]) != value) _world.SetShared(_entities[i], value);
        }
        long total = 0;
        using (var values = _world.OpenRead<SharedNumber>())
            for (int i = 0; i < Lookups; i++) total += values.ReadKnown(_lookupEntities[i]).Value;
        int count = 0;
        foreach (Query query in _sharedQueries!) count += query.Count;
        ReadTotal = total;
        SharedQueryCount = count;
        SharedValueCount = _world.SharedValueCount<SharedNumber>();
    }
}

internal sealed partial class EasyEcsWorld
{
    private ReferenceSharedValues<SharedNumber>? _sharedValues;
    private EasyEcs.ValueIndex<SharedNumberId, int>? _sharedIndex;
    private void PrepareShared(string workload)
    {
        if (_sharedValues is not null) return;
        SharedGroupCount = workload.EndsWith("unique") ? Count : 16;
        SharedChanges = workload.Contains("change");
        _sharedValues = new();
        for (int i = 0; i < SharedGroupCount; i++) _sharedValues.Intern(new SharedNumber(i));
        if (!_entities[0].Has<SharedNumberId>())
            for (int i = 0; i < Count; i++) _entities[i].Set(new SharedNumberId(i % SharedGroupCount));
        _sharedIndex = _world.Index<SharedNumberId, int>();
        _sharedIndex.Count(0);
    }
    internal override void SharedCycle()
    {
        if (SharedChanges) SharedShift ^= 1;
        for (int i = 0; i < Count; i += 4)
        {
            var value = new SharedNumber((i + SharedShift) % SharedGroupCount);
            int current = _entities[i].Read<SharedNumberId>().Value;
            if (_sharedValues!.Values[current] != value) _entities[i].Set(new SharedNumberId(_sharedValues.Intern(value)));
        }
        long total = 0;
        for (int i = 0; i < Lookups; i++) total += _sharedValues!.Values[_lookupEntities[i].Read<SharedNumberId>().Value].Value;
        int count = 0;
        for (int i = 0; i < 8; i++) count += _sharedIndex!.Count(SharedGroupCount / 2 - 4 + i);
        ReadTotal = total;
        SharedQueryCount = count;
        SharedValueCount = _sharedValues!.Count;
    }
}

internal sealed partial class FrifloWorld
{
    private ReferenceSharedValues<SharedNumber>? _sharedValues;
    private ArchetypeQuery<SharedNumberId>[]? _sharedQueries;
    private void PrepareShared(string workload)
    {
        if (_sharedValues is not null) return;
        SharedGroupCount = workload.EndsWith("unique") ? Count : 16;
        SharedChanges = workload.Contains("change");
        _sharedValues = new();
        for (int i = 0; i < SharedGroupCount; i++) _sharedValues.Intern(new SharedNumber(i));
        if (!_entities[0].HasComponent<SharedNumberId>())
            for (int i = 0; i < Count; i++) _entities[i].AddComponent(new SharedNumberId(i % SharedGroupCount));
        _sharedQueries = new ArchetypeQuery<SharedNumberId>[8];
        for (int i = 0; i < 8; i++) _sharedQueries[i] = _world.Query<SharedNumberId>().HasValue<SharedNumberId, int>(SharedGroupCount / 2 - 4 + i);
    }
    internal override void SharedCycle()
    {
        if (SharedChanges) SharedShift ^= 1;
        for (int i = 0; i < Count; i += 4)
        {
            var value = new SharedNumber((i + SharedShift) % SharedGroupCount);
            int current = _entities[i].GetComponent<SharedNumberId>().Value;
            if (_sharedValues!.Values[current] != value) _entities[i].AddComponent(new SharedNumberId(_sharedValues.Intern(value)));
        }
        long total = 0;
        for (int i = 0; i < Lookups; i++) total += _sharedValues!.Values[_lookupEntities[i].GetComponent<SharedNumberId>().Value].Value;
        int count = 0;
        foreach (var query in _sharedQueries!) count += query.Count;
        ReadTotal = total;
        SharedQueryCount = count;
        SharedValueCount = _sharedValues!.Count;
    }
}

internal sealed partial class ArchWorld
{
    private ReferenceSharedValues<SharedNumber>? _sharedValues;
    private ArchSharedGroups? _sharedGroups;
    private void PrepareShared(string workload)
    {
        if (_sharedValues is not null) return;
        SharedGroupCount = workload.EndsWith("unique") ? Count : 16;
        SharedChanges = workload.Contains("change");
        _sharedValues = new();
        _sharedGroups = new(Count + Changes);
        for (int i = 0; i < SharedGroupCount; i++) _sharedValues.Intern(new SharedNumber(i));
        bool declared = _world.Has<SharedNumberId>(_entities[0]);
        for (int i = 0; i < Count; i++)
        {
            int id = i % SharedGroupCount;
            if (!declared) _world.Add(_entities[i], new SharedNumberId(id));
            _sharedGroups.Set(_entities[i], id);
        }
    }
    private int ReadSharedId(Arch.Core.Entity entity)
    {
        if (!_world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
        ref SharedNumberId value = ref _world.TryGetRef<SharedNumberId>(entity, out bool exists);
        if (!exists) throw new ArgumentException("Shared component is absent.");
        return value.Value;
    }
    internal override void SharedCycle()
    {
        if (SharedChanges) SharedShift ^= 1;
        for (int i = 0; i < Count; i += 4)
        {
            var value = new SharedNumber((i + SharedShift) % SharedGroupCount);
            int current = ReadSharedId(_entities[i]);
            if (_sharedValues!.Values[current] == value) continue;
            int id = _sharedValues.Intern(value);
            _world.Set(_entities[i], new SharedNumberId(id));
            _sharedGroups!.Set(_entities[i], id);
        }
        long total = 0;
        for (int i = 0; i < Lookups; i++) total += _sharedValues!.Values[ReadSharedId(_lookupEntities[i])].Value;
        int count = 0;
        for (int i = 0; i < 8; i++) count += _sharedGroups!.Count(SharedGroupCount / 2 - 4 + i);
        ReadTotal = total;
        SharedQueryCount = count;
        SharedValueCount = _sharedValues!.Count;
    }
}

internal sealed partial class DefaultEcsWorld
{
    private ReferenceSharedValues<SharedNumber>? _sharedValues;
    private DefaultEcs.EntityMultiMap<SharedNumberId>? _sharedGroups;
    private void PrepareShared(string workload)
    {
        if (_sharedValues is not null) return;
        SharedGroupCount = workload.EndsWith("unique") ? Count : 16;
        SharedChanges = workload.Contains("change");
        _sharedValues = new();
        for (int i = 0; i < SharedGroupCount; i++) _sharedValues.Intern(new SharedNumber(i));
        for (int i = 0; i < Count; i++) _entities[i].Set(new SharedNumberId(i % SharedGroupCount));
        _sharedGroups = _world.GetEntities().With<SharedNumberId>().AsMultiMap<SharedNumberId>();
    }
    private static int ReadSharedId(DefaultEcs.Entity entity)
    {
        if (!entity.IsAlive || !entity.Has<SharedNumberId>()) throw new ArgumentException("Entity component is absent.");
        return entity.Get<SharedNumberId>().Value;
    }
    internal override void SharedCycle()
    {
        if (SharedChanges) SharedShift ^= 1;
        for (int i = 0; i < Count; i += 4)
        {
            var value = new SharedNumber((i + SharedShift) % SharedGroupCount);
            int current = ReadSharedId(_entities[i]);
            if (_sharedValues!.Values[current] != value) _entities[i].Set(new SharedNumberId(_sharedValues.Intern(value)));
        }
        long total = 0;
        for (int i = 0; i < Lookups; i++) total += _sharedValues!.Values[ReadSharedId(_lookupEntities[i])].Value;
        int count = 0;
        for (int i = 0; i < 8; i++)
            if (_sharedGroups!.TryGetEntities(new SharedNumberId(SharedGroupCount / 2 - 4 + i), out var entities)) count += entities.Length;
        ReadTotal = total;
        SharedQueryCount = count;
        SharedValueCount = _sharedValues!.Count;
    }
}
