using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public interface IIndexedComponent<TKey> : IComponent where TKey : notnull
{
    TKey GetKey();
}

internal struct Membership
{
    internal int GroupPlusOne, Place;
}

internal struct KeyGroup<TKey> where TKey : notnull
{
    internal Entity[]? Items;
    internal TKey Key;
    internal uint Hash;
    internal int Next, Count;
}

internal interface IValueIndex
{
    void Reserve(int capacity);
    void Remove(int entityId);
    void AddFromEntity(Entity entity, in ColumnEntry column, int row);
    void Clear();
}

internal interface IValueIndex<T> : IValueIndex where T : struct
{
    void Set(Entity entity, in T value, int sharedId = -1);
    void Added(Entity entity, in T value);
}

public sealed class ValueIndex<T, TKey> : IValueIndex<T>
    where T : struct, IIndexedComponent<TKey> where TKey : notnull
{
    private readonly World _world;
    private readonly IComparer<TKey> _compare;
    private int[] _buckets = new int[16];
    private KeyGroup<TKey>[] _groups = new KeyGroup<TKey>[16];
    private Membership[] _members;
    private int[]? _ordered;
    private int _used, _free, _groupCount;

    internal ValueIndex(World world, int capacity, IComparer<TKey>? compare)
        => (_world, _members, _compare) = (world, new Membership[capacity], compare ?? Comparer<TKey>.Default);

    public int KeyCount => _groupCount;

    private int FindGroup(TKey key, uint hash)
    {
        for (int next = _buckets[hash & (uint)(_buckets.Length - 1)]; next != 0; next = _groups[next - 1].Next)
        {
            int id = next - 1;
            ref KeyGroup<TKey> group = ref _groups[id];
            if (group.Hash == hash && EqualityComparer<TKey>.Default.Equals(group.Key, key)) return id;
        }
        return -1;
    }

    void IValueIndex<T>.Set(Entity entity, in T value, int sharedId) => Put(entity, value, true, sharedId);
    void IValueIndex<T>.Added(Entity entity, in T value) => Put(entity, value, false, -1);
    void IValueIndex.AddFromEntity(Entity entity, in ColumnEntry column, int row)
    {
        if (ComponentType<T>.IsSharedType)
        {
            ref readonly T value = ref _world.ReadSharedColumn<T>(column, row);
            Put(entity, in value, false, -1);
        }
        else if (ComponentType<T>.IsTagType) Put(entity, default, false, -1);
        else
        {
            ref readonly T value = ref ColumnEntry.At<T>(ref Unsafe.AsRef(in column), row);
            Put(entity, in value, false, -1);
        }
    }

    private void Put(Entity entity, in T value, bool write, int sharedId)
    {
        using MembershipEdit membership = write ? _world.BeginMembershipEdit(entity) : default;
        _world.RequireStructure();
        ref EntityRecord record = ref _world.Resolve(entity);
        bool active = World.IndexComponentActive<T>(in record);
        if (!active)
        {
            if (write) _world.SetIndexedValue(entity, ref record, value, sharedId);
            return;
        }
        TKey key = value.GetKey();
        Membership member = _members[entity.Id];
        int old = member.GroupPlusOne - 1;
        if (old >= 0 && EqualityComparer<TKey>.Default.Equals(_groups[old].Key, key))
        {
            if (write) _world.SetIndexedValue(entity, ref record, value, sharedId);
            return;
        }
        uint hash = (uint)EqualityComparer<TKey>.Default.GetHashCode(key);
        int target = FindGroup(key, hash);
        bool oldEmpty = old >= 0 && _groups[old].Count == 1;
        int oldOrder = _ordered is not null && oldEmpty ? FindOrder(_groups[old].Key, old) : -1;
        if (target < 0 && oldEmpty)
        {
            int newOrder = _ordered is null ? -1 : FindOrder(key, old);
            if (write) _world.SetIndexedValue(entity, ref record, value, sharedId);
            Unlink(old);
            ref KeyGroup<TKey> reused = ref _groups[old];
            reused.Key = key;
            reused.Hash = hash;
            int bucket = (int)(hash & (uint)(_buckets.Length - 1));
            reused.Next = _buckets[bucket];
            _buckets[bucket] = old + 1;
            if (_ordered is not null)
            {
                RemoveOrder(oldOrder, _groupCount);
                if (newOrder > oldOrder) newOrder--;
                InsertOrder(newOrder, old, _groupCount - 1);
            }
            return;
        }
        bool create = target < 0;
        Entity[]? newItems = null;
        int insertOrder = -1;
        if (create)
        {
            target = _free != 0 ? _free - 1 : _used;
            if (_ordered is not null) insertOrder = FindOrder(key, target);
            if (target == _groups.Length)
            {
                Array.Resize(ref _groups, _groups.Length * 2);
                if (_ordered is not null) Array.Resize(ref _ordered, _groups.Length);
            }
            if ((_groupCount + 1) * 4 > _buckets.Length * 3) GrowBuckets();
            newItems = ArrayPool<Entity>.Shared.Rent(4);
        }
        else
        {
            ref KeyGroup<TKey> destination = ref _groups[target];
            if (destination.Count == destination.Items!.Length)
            {
                Entity[] items = ArrayPool<Entity>.Shared.Rent(destination.Count * 2);
                destination.Items.AsSpan(0, destination.Count).CopyTo(items);
                ArrayPool<Entity>.Shared.Return(destination.Items);
                destination.Items = items;
            }
        }
        if (write) _world.SetIndexedValue(entity, ref record, value, sharedId);
        if (create)
        {
            if (_free != 0) _free = _groups[target].Next; else _used++;
        }
        if (old >= 0) Drop(entity.Id, member, oldOrder);
        if (create)
        {
            int bucket = (int)(hash & (uint)(_buckets.Length - 1));
            _groups[target] = new() { Key = key, Hash = hash, Next = _buckets[bucket], Items = newItems };
            _buckets[bucket] = target + 1;
            _groupCount++;
        }
        ref KeyGroup<TKey> group = ref _groups[target];
        int place = group.Count++;
        group.Items![place] = entity;
        _members[entity.Id] = new() { GroupPlusOne = target + 1, Place = place };
        if (create && _ordered is not null)
        {
            if (oldEmpty && insertOrder > oldOrder) insertOrder--;
            InsertOrder(insertOrder, target, _groupCount - 1);
        }
    }

    void IValueIndex.Remove(int entityId)
    {
        Membership member = _members[entityId];
        if (member.GroupPlusOne == 0) return;
        int group = member.GroupPlusOne - 1;
        int order = _ordered is not null && _groups[group].Count == 1 ? FindOrder(_groups[group].Key, group) : -1;
        Drop(entityId, member, order);
        _members[entityId] = default;
    }

    private void Drop(int entityId, Membership member, int order)
    {
        int id = member.GroupPlusOne - 1;
        ref KeyGroup<TKey> group = ref _groups[id];
        int last = --group.Count;
        if (member.Place != last)
        {
            Entity moved = group.Items![last];
            group.Items[member.Place] = moved;
            _members[moved.Id].Place = member.Place;
        }
        if (last != 0) return;
        if (_ordered is not null) RemoveOrder(order, _groupCount);
        Unlink(id);
        ArrayPool<Entity>.Shared.Return(group.Items!);
        group = new() { Next = _free };
        _free = id + 1;
        _groupCount--;
    }

    private void Unlink(int id)
    {
        int bucket = (int)(_groups[id].Hash & (uint)(_buckets.Length - 1));
        int previous = -1;
        for (int next = _buckets[bucket] - 1; next != id; next = _groups[next].Next - 1) previous = next;
        if (previous < 0) _buckets[bucket] = _groups[id].Next;
        else _groups[previous].Next = _groups[id].Next;
    }

    private void GrowBuckets()
    {
        var buckets = new int[_buckets.Length * 2];
        for (int i = 0; i < _used; i++)
        {
            ref KeyGroup<TKey> group = ref _groups[i];
            if (group.Count == 0) continue;
            int bucket = (int)(group.Hash & (uint)(buckets.Length - 1));
            group.Next = buckets[bucket];
            buckets[bucket] = i + 1;
        }
        _buckets = buckets;
    }

    private int FindOrder(TKey key, int id)
    {
        int low = 0, high = _groupCount;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            int other = _ordered![middle];
            int comparison = _compare.Compare(_groups[other].Key, key);
            if (comparison < 0 || (comparison == 0 && other < id)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void RemoveOrder(int position, int count)
        => _ordered!.AsSpan(position + 1, count - position - 1).CopyTo(_ordered.AsSpan(position));
    private void InsertOrder(int position, int group, int count)
    {
        _ordered!.AsSpan(position, count - position).CopyTo(_ordered.AsSpan(position + 1));
        _ordered![position] = group;
    }

    private void EnsureOrdered()
    {
        if (_ordered is not null) return;
        int[] ordered = new int[_groups.Length];
        int count = 0;
        for (int i = 0; i < _used; i++) if (_groups[i].Count != 0) ordered[count++] = i;
        Array.Sort(ordered, 0, count, Comparer<int>.Create((a, b) =>
        {
            int comparison = _compare.Compare(_groups[a].Key, _groups[b].Key);
            return comparison != 0 ? comparison : a.CompareTo(b);
        }));
        _ordered = ordered;
    }

    private int Bound(TKey key, bool upper)
    {
        int low = 0, high = _groupCount;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            int comparison = _compare.Compare(_groups[_ordered![middle]].Key, key);
            if (comparison < 0 || (upper && comparison == 0)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public IndexMembers Find(TKey key)
    {
        _world.BeginStructureRead();
        try
        {
            int id = FindGroup(key, (uint)EqualityComparer<TKey>.Default.GetHashCode(key));
            return id < 0 ? new(_world, ReadOnlySpan<Entity>.Empty)
                : new(_world, _groups[id].Items.AsSpan(0, _groups[id].Count));
        }
        catch { _world.EndValueRead(); throw; }
    }

    public IndexGroups<TKey> Range(TKey lower, TKey upper)
    {
        _world.BeginStructureRead();
        try
        {
            EnsureOrdered();
            return new(_world, _groups, _ordered!, Bound(lower, false), Bound(upper, true));
        }
        catch { _world.EndValueRead(); throw; }
    }

    public IndexGroups<TKey> Groups()
    {
        _world.BeginStructureRead();
        try { EnsureOrdered(); return new(_world, _groups, _ordered!, 0, _groupCount); }
        catch { _world.EndValueRead(); throw; }
    }

    void IValueIndex.Reserve(int capacity) => Array.Resize(ref _members, capacity);
    void IValueIndex.Clear()
    {
        for (int i = 0; i < _used; i++)
            if (_groups[i].Items is { } items) ArrayPool<Entity>.Shared.Return(items);
        Array.Clear(_groups, 0, _used);
        Array.Clear(_members, 0, Math.Min(_members.Length, _world.Used + 1));
        Array.Clear(_buckets);
        _used = _free = _groupCount = 0;
    }
}

public readonly ref struct IndexMembers
{
    private readonly World _world;
    public ReadOnlySpan<Entity> Entities { get; }
    internal IndexMembers(World world, ReadOnlySpan<Entity> entities) { _world = world; Entities = entities; }
    public int Count => Entities.Length;
    public ReadOnlySpan<Entity>.Enumerator GetEnumerator() => Entities.GetEnumerator();
    public void Dispose() => _world.EndValueRead();
}

public readonly ref struct IndexGroup<TKey> where TKey : notnull
{
    public TKey Key { get; }
    public ReadOnlySpan<Entity> Entities { get; }
    internal IndexGroup(KeyGroup<TKey> group) { Key = group.Key; Entities = group.Items.AsSpan(0, group.Count); }
}

public readonly ref struct IndexGroups<TKey> where TKey : notnull
{
    private readonly World _world;
    private readonly KeyGroup<TKey>[] _groups;
    private readonly int[] _ordered;
    private readonly int _start, _end;
    internal IndexGroups(World world, KeyGroup<TKey>[] groups, int[] ordered, int start, int end)
        => (_world, _groups, _ordered, _start, _end) = (world, groups, ordered, start, end);
    public IndexGroupEnumerator<TKey> GetEnumerator() => new(_groups, _ordered, _start, _end);
    public void Dispose() => _world.EndValueRead();
}

public struct IndexGroupEnumerator<TKey> where TKey : notnull
{
    private readonly KeyGroup<TKey>[] _groups;
    private readonly int[] _ordered;
    private readonly int _end;
    private int _index;
    internal IndexGroupEnumerator(KeyGroup<TKey>[] groups, int[] ordered, int start, int end)
        => (_groups, _ordered, _index, _end) = (groups, ordered, start - 1, end);
    public bool MoveNext() => ++_index < _end;
    public readonly IndexGroup<TKey> Current => new(_groups[_ordered[_index]]);
}
