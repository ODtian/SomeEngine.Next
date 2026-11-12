using System.Buffers;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

// Only main targets that differ from their applied targets enter this index.
internal sealed class DifferenceTargets(int capacity)
{
    private struct Member { internal int GroupPlusOne, Place, Generation; }
    private struct Group
    {
        internal Entity Target, First;
        internal Entity[]? Items;
        internal uint Hash;
        internal int Count, Next;
    }

    private Member[] _members = new Member[capacity];
    private Group[] _groups = new Group[16];
    private int[] _buckets = new int[16];
    private int _used, _free;

    internal void Reserve(int capacity)
    {
        if (capacity > _members.Length) Array.Resize(ref _members, capacity);
    }

    private int Find(Entity target, uint hash)
    {
        for (int next = _buckets[hash & (uint)(_buckets.Length - 1)]; next != 0; next = _groups[next - 1].Next)
            if (_groups[next - 1].Hash == hash && _groups[next - 1].Target == target) return next - 1;
        return -1;
    }

    internal ReadOnlySpan<Entity> Members(Entity target)
    {
        int group = Find(target, (uint)target.GetHashCode());
        if (group < 0) return [];
        ref Group found = ref _groups[group];
        return found.Count == 1 ? new ReadOnlySpan<Entity>(in found.First) : found.Items.AsSpan(0, found.Count);
    }

    internal void Set(Entity entity, Entity target)
    {
        if (target == Entity.Null) { Remove(entity); return; }
        ref Member member = ref _members[entity.Id];
        if (member.GroupPlusOne != 0 && member.Generation == entity.Generation
            && _groups[member.GroupPlusOne - 1].Target == target) return;
        Remove(entity);
        uint hash = (uint)target.GetHashCode();
        int id = Find(target, hash);
        if (id < 0)
        {
            if (_free != 0) { id = _free - 1; _free = _groups[id].Next; }
            else
            {
                id = _used++;
                if (id == _groups.Length) Array.Resize(ref _groups, _groups.Length * 2);
                if (_used * 4 > _buckets.Length * 3) GrowBuckets();
            }
            int bucket = (int)(hash & (uint)(_buckets.Length - 1));
            _groups[id] = new() { Target = target, Hash = hash, Next = _buckets[bucket] };
            _buckets[bucket] = id + 1;
        }
        ref Group group = ref _groups[id];
        int place = group.Count;
        if (place == 0) group.First = entity;
        else
        {
            if (place == 1)
            {
                group.Items = ArrayPool<Entity>.Shared.Rent(4);
                group.Items[0] = group.First;
                group.First = default;
            }
            else if (place == group.Items!.Length)
            {
                Entity[] items = ArrayPool<Entity>.Shared.Rent(place * 2);
                group.Items.AsSpan(0, place).CopyTo(items);
                ArrayPool<Entity>.Shared.Return(group.Items);
                group.Items = items;
            }
            group.Items![place] = entity;
        }
        group.Count = place + 1;
        member = new() { GroupPlusOne = id + 1, Place = place, Generation = entity.Generation };
    }

    internal void Remove(Entity entity)
    {
        ref Member member = ref _members[entity.Id];
        if (member.GroupPlusOne == 0 || member.Generation != entity.Generation) return;
        int id = member.GroupPlusOne - 1;
        ref Group group = ref _groups[id];
        int last = --group.Count;
        if (last != 0 && last != member.Place)
        {
            Entity moved = group.Items![last];
            group.Items[member.Place] = moved;
            _members[moved.Id].Place = member.Place;
        }
        member = default;
        if (last != 0)
        {
            if (last == 1)
            {
                group.First = group.Items![0];
                ArrayPool<Entity>.Shared.Return(group.Items);
                group.Items = null;
            }
            return;
        }
        int bucket = (int)(group.Hash & (uint)(_buckets.Length - 1));
        int previous = -1;
        for (int next = _buckets[bucket] - 1; next != id; next = _groups[next].Next - 1) previous = next;
        if (previous < 0) _buckets[bucket] = group.Next;
        else _groups[previous].Next = group.Next;
        group = new() { Next = _free };
        _free = id + 1;
    }

    private void GrowBuckets()
    {
        var buckets = new int[_buckets.Length * 2];
        for (int i = 0; i < _used; i++)
        {
            ref Group group = ref _groups[i];
            if (group.Count == 0) continue;
            int bucket = (int)(group.Hash & (uint)(buckets.Length - 1));
            group.Next = buckets[bucket];
            buckets[bucket] = i + 1;
        }
        _buckets = buckets;
    }

    internal void Clear()
    {
        for (int i = 0; i < _used; i++)
        {
            Group group = _groups[i];
            if (group.Count == 1) _members[group.First.Id] = default;
            else if (group.Items is { } items)
            {
                foreach (Entity entity in items.AsSpan(0, group.Count)) _members[entity.Id] = default;
                ArrayPool<Entity>.Shared.Return(items);
            }
        }
        Array.Clear(_groups, 0, _used); Array.Clear(_buckets);
        _used = _free = 0;
    }
}
