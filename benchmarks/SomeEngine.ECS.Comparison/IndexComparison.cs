using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public readonly record struct IndexedNumber(int Value, int Ordinal) : IIndexedComponent<int>, EasyEcs.IComponent, EasyEcs.IIndexed<int>,
    Friflo.Engine.ECS.IComponent, Friflo.Engine.ECS.IIndexedComponent<int>
{
    public int GetKey() { IndexProjection.Calls++; return Value; }
    int EasyEcs.IIndexed<int>.Key => GetKey();
    int Friflo.Engine.ECS.IIndexedComponent<int>.GetIndexedValue() => Value;
}

internal static class IndexProjection
{
    internal static int Calls;
}

public readonly struct ReferenceIndexedNumber(int key, int ordinal) : Friflo.Engine.ECS.IComponent,
    Friflo.Engine.ECS.IIndexedComponent<int>, IEquatable<ReferenceIndexedNumber>
{
    public readonly int Key = key;
    public readonly int Ordinal = ordinal;
    public int GetIndexedValue() => Key;
    public bool Equals(ReferenceIndexedNumber other) => Key == other.Key;
    public override bool Equals(object? other) => other is ReferenceIndexedNumber value && Equals(value);
    public override int GetHashCode() => Key;
    internal static ReferenceIndexedNumber Project(in IndexedNumber value) => new(value.GetKey(), value.Ordinal);
}

internal sealed class ReferenceEntityIndex<TEntity> where TEntity : struct
{
    private sealed class Group
    {
        internal TEntity[] Items = new TEntity[4];
        internal int Count;
    }
    private struct Member { internal Group? Group; internal int Place; }
    private readonly Dictionary<int, Group> _groups = new();
    private Member[] _members;
    private readonly Func<TEntity, int> _entityId;
    private int[] _keys = new int[16];
    private int _keyCount;
    private bool _keysChanged;

    internal ReferenceEntityIndex(int capacity, Func<TEntity, int> entityId)
        => (_members, _entityId) = (new Member[capacity], entityId);

    internal void Set(TEntity entity, int key)
    {
        int id = _entityId(entity);
        if (id >= _members.Length) Array.Resize(ref _members, Math.Max(id + 1, _members.Length * 2));
        ref Member member = ref _members[id];
        _groups.TryGetValue(key, out Group? target);
        if (ReferenceEquals(member.Group, target) && target is not null) return;
        if (target is null)
        {
            target = new();
            _groups.Add(key, target);
            _keysChanged = true;
        }
        if (target.Count == target.Items.Length) Array.Resize(ref target.Items, target.Count * 2);
        if (target.Count == 0) _keysChanged = true;
        if (member.Group is { } previous)
        {
            int last = --previous.Count;
            if (member.Place != last)
            {
                TEntity moved = previous.Items[last];
                previous.Items[member.Place] = moved;
                _members[_entityId(moved)].Place = member.Place;
            }
            if (last == 0) _keysChanged = true;
        }
        int place = target.Count++;
        target.Items[place] = entity;
        member = new() { Group = target, Place = place };
    }

    internal void Remove(TEntity entity)
    {
        ref Member member = ref _members[_entityId(entity)];
        Group group = member.Group!;
        int last = --group.Count;
        if (member.Place != last)
        {
            TEntity moved = group.Items[last];
            group.Items[member.Place] = moved;
            _members[_entityId(moved)].Place = member.Place;
        }
        if (last == 0) _keysChanged = true;
        member = default;
    }

    internal ReadOnlySpan<TEntity> Find(int key) => _groups.TryGetValue(key, out Group? group)
        ? group.Items.AsSpan(0, group.Count) : ReadOnlySpan<TEntity>.Empty;

    internal ReadOnlySpan<int> Keys()
    {
        if (_keysChanged)
        {
            if (_keys.Length < _groups.Count) Array.Resize(ref _keys, _groups.Count * 2);
            _keyCount = 0;
            foreach (var pair in _groups) if (pair.Value.Count != 0) _keys[_keyCount++] = pair.Key;
            Array.Sort(_keys, 0, _keyCount);
            _keysChanged = false;
        }
        return _keys.AsSpan(0, _keyCount);
    }
}

internal sealed partial class SomeEngineWorld
{
    private ValueIndex<IndexedNumber, int>? _numberIndex;
    private void PrepareIndex(string workload)
    {
        if (_numberIndex is not null) return;
        PrepareIndexResult(workload);
        if (!_world.Has<IndexedNumber>(_entities[0]))
            for (int i = 0; i < Count; i++) _world.Set(_entities[i], new IndexedNumber(i % 16, i));
        _numberIndex = _world.Index<IndexedNumber, int>();
        using var ordered = _numberIndex.Groups();
    }

    internal override void IndexCycle()
    {
        BeginIndexCycle();
        for (int i = 0; i < Count; i += 4) _world.Set(_entities[i], new IndexedNumber((i + IndexShift) % 16, i));
        IndexProjectionCalls = IndexProjection.Calls;
        using (var found = _numberIndex!.Find(5))
            foreach (var entity in found) ObserveIndexFind(_world.Read<IndexedNumber>(entity).Ordinal);
        using (var range = _numberIndex.Range(4, 11))
            foreach (var group in range)
                foreach (var entity in group.Entities)
                {
                    ref readonly var value = ref _world.Read<IndexedNumber>(entity);
                    ObserveIndexRange(group.Key, value.Value, value.Ordinal);
                }
        IndexKeyCount = _numberIndex.KeyCount;
    }
}

internal sealed partial class EasyEcsWorld
{
    private EasyEcs.ValueIndex<IndexedNumber, int>? _numberIndex;
    private void PrepareIndex(string workload)
    {
        if (_numberIndex is not null) return;
        PrepareIndexResult(workload);
        if (!_entities[0].Has<IndexedNumber>())
            for (int i = 0; i < Count; i++) _entities[i].Set(new IndexedNumber(i % 16, i));
        _numberIndex = _world.Index<IndexedNumber, int>();
        var order = EasyEcs.OrderedIndexExtensions.Ascending(_numberIndex).GetEnumerator();
        order.MoveNext();
    }

    internal override void IndexCycle()
    {
        BeginIndexCycle();
        for (int i = 0; i < Count; i += 4) _entities[i].Set(new IndexedNumber((i + IndexShift) % 16, i));
        IndexProjectionCalls = IndexProjection.Calls;
        foreach (var entity in _numberIndex!.Find(5)) ObserveIndexFind(entity.Read<IndexedNumber>().Ordinal);
        var range = EasyEcs.OrderedIndexExtensions.Range(_numberIndex, 4, 11).GetEnumerator();
        while (range.MoveNext())
        {
            ref readonly var value = ref range.Current.Read<IndexedNumber>();
            ObserveIndexRange(range.Key, value.Value, value.Ordinal);
        }
        for (int key = 0; key < 16; key++) if (_numberIndex.Count(key) != 0) IndexKeyCount++;
    }
}

internal sealed partial class FrifloWorld
{
    private ComponentIndex<ReferenceIndexedNumber, int> _numberIndex;
    private bool _numbersReady;
    private int[] _numberKeys = new int[16];
    private void PrepareIndex(string workload)
    {
        if (_numbersReady) return;
        PrepareIndexResult(workload);
        if (!_entities[0].HasComponent<ReferenceIndexedNumber>())
            for (int i = 0; i < Count; i++) _entities[i].AddComponent(ReferenceIndexedNumber.Project(new IndexedNumber(i % 16, i)));
        _numberIndex = _world.ComponentIndex<ReferenceIndexedNumber, int>();
        _numbersReady = true;
    }
    internal override void IndexCycle()
    {
        BeginIndexCycle();
        for (int i = 0; i < Count; i += 4)
            _entities[i].AddComponent(ReferenceIndexedNumber.Project(new IndexedNumber((i + IndexShift) % 16, i)));
        IndexProjectionCalls = IndexProjection.Calls;
        foreach (var entity in _numberIndex[5]) ObserveIndexFind(entity.GetComponent<ReferenceIndexedNumber>().Ordinal);
        foreach (int key in _numberIndex.Values) _numberKeys[IndexKeyCount++] = key;
        Array.Sort(_numberKeys, 0, IndexKeyCount);
        foreach (int key in _numberKeys.AsSpan(0, IndexKeyCount))
        {
            if (key < 4 || key > 11) continue;
            foreach (var entity in _numberIndex[key])
            {
                ref readonly var value = ref entity.GetComponent<ReferenceIndexedNumber>();
                ObserveIndexRange(key, value.Key, value.Ordinal);
            }
        }
    }
}

internal sealed partial class ArchWorld
{
    private ReferenceEntityIndex<Arch.Core.Entity>? _numberIndex;
    private void PrepareIndex(string workload)
    {
        if (_numberIndex is not null) return;
        PrepareIndexResult(workload);
        _numberIndex = new(Count + Changes, static entity => entity.Id);
        bool declared = _world.Has<ReferenceIndexedNumber>(_entities[0]);
        for (int i = 0; i < Count; i++)
        {
            if (!declared) _world.Add(_entities[i], ReferenceIndexedNumber.Project(new IndexedNumber(i % 16, i)));
            _numberIndex.Set(_entities[i], i % 16);
        }
    }
    internal override void IndexCycle()
    {
        BeginIndexCycle();
        for (int i = 0; i < Count; i += 4)
        {
            var value = ReferenceIndexedNumber.Project(new IndexedNumber((i + IndexShift) % 16, i));
            _world.Set(_entities[i], value);
            _numberIndex!.Set(_entities[i], value.Key);
        }
        IndexProjectionCalls = IndexProjection.Calls;
        foreach (var entity in _numberIndex!.Find(5)) ObserveIndexFind(_world.Get<ReferenceIndexedNumber>(entity).Ordinal);
        var keys = _numberIndex.Keys();
        IndexKeyCount = keys.Length;
        foreach (int key in keys)
        {
            if (key < 4 || key > 11) continue;
            foreach (var entity in _numberIndex.Find(key))
            {
                ref readonly var value = ref _world.Get<ReferenceIndexedNumber>(entity);
                ObserveIndexRange(key, value.Key, value.Ordinal);
            }
        }
    }
}

internal sealed partial class DefaultEcsWorld
{
    private DefaultEcs.EntityMultiMap<ReferenceIndexedNumber>? _numberIndex;
    private int[] _numberKeys = new int[16];
    private void PrepareIndex(string workload)
    {
        if (_numberIndex is not null) return;
        PrepareIndexResult(workload);
        for (int i = 0; i < Count; i++) _entities[i].Set(ReferenceIndexedNumber.Project(new IndexedNumber(i % 16, i)));
        _numberIndex = _world.GetEntities().With<ReferenceIndexedNumber>().AsMultiMap<ReferenceIndexedNumber>();
    }
    internal override void IndexCycle()
    {
        BeginIndexCycle();
        for (int i = 0; i < Count; i += 4)
            _entities[i].Set(ReferenceIndexedNumber.Project(new IndexedNumber((i + IndexShift) % 16, i)));
        IndexProjectionCalls = IndexProjection.Calls;
        if (_numberIndex!.TryGetEntities(new(5, 0), out var found))
            foreach (var entity in found) ObserveIndexFind(entity.Get<ReferenceIndexedNumber>().Ordinal);
        foreach (var key in _numberIndex.Keys) _numberKeys[IndexKeyCount++] = key.Key;
        Array.Sort(_numberKeys, 0, IndexKeyCount);
        foreach (int key in _numberKeys.AsSpan(0, IndexKeyCount))
        {
            if (key < 4 || key > 11) continue;
            if (_numberIndex.TryGetEntities(new(key, 0), out var members))
                foreach (var entity in members)
                {
                    ref readonly var value = ref entity.Get<ReferenceIndexedNumber>();
                    ObserveIndexRange(key, value.Key, value.Ordinal);
                }
        }
    }
}
