using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Shared;

[InlineArray(2)]
internal struct SharedPair { private Entity _first; }

internal struct SharedGroup
{
    internal SharedPair Small;
    internal Entity[]? Items;
    internal int Count;
}

internal sealed class SharedGroups(int capacity)
{
    private SharedGroup[] _groups = new SharedGroup[16];
    private int[] _places = new int[capacity];

    internal void ReserveEntities(int capacity) => Array.Resize(ref _places, capacity);
    internal void ReserveValues(int capacity)
    {
        if (capacity > _groups.Length) Array.Resize(ref _groups, capacity);
    }

    internal int Count(int value)
    {
        if ((uint)value >= (uint)_groups.Length) return 0;
        ref SharedGroup group = ref _groups[value];
        return group.Count;
    }

    internal ReadOnlySpan<Entity> Members(int value)
    {
        if ((uint)value >= (uint)_groups.Length) return ReadOnlySpan<Entity>.Empty;
        ref SharedGroup group = ref _groups[value];
        return group.Items is { } items ? items.AsSpan(0, group.Count) : group.Small[..group.Count];
    }

    internal void Set(Entity entity, int oldValue, int value, bool active)
    {
        if (!active || oldValue == value) return;
        ref SharedGroup target = ref _groups[value];
        if (target.Count == 2 && target.Items is null)
        {
            target.Items = ArrayPool<Entity>.Shared.Rent(4);
            target.Small[..2].CopyTo(target.Items);
        }
        else if (target.Items is { } items && target.Count == items.Length)
        {
            var grown = ArrayPool<Entity>.Shared.Rent(target.Count * 2);
            items.AsSpan(0, target.Count).CopyTo(grown);
            ArrayPool<Entity>.Shared.Return(items);
            target.Items = grown;
        }
        if (oldValue >= 0) Remove(entity.Id, oldValue, active);
        int place = target.Count++;
        if (target.Items is { } destination) destination[place] = entity;
        else target.Small[place] = entity;
        _places[entity.Id] = place;
    }

    internal void Remove(int entity, int value, bool active)
    {
        if (!active) return;
        ref SharedGroup group = ref _groups[value];
        int place = _places[entity];
        int last = --group.Count;
        if (place != last)
        {
            Entity moved;
            if (group.Items is { } items) { moved = items[last]; items[place] = moved; }
            else { moved = group.Small[last]; group.Small[place] = moved; }
            _places[moved.Id] = place;
        }
        if (last == 0 && group.Items is { } returned)
        {
            ArrayPool<Entity>.Shared.Return(returned);
            group.Items = null;
        }
    }

    internal void Clear()
    {
        for (int i = 0; i < _groups.Length; i++)
            if (_groups[i].Items is { } items) ArrayPool<Entity>.Shared.Return(items);
        Array.Clear(_groups);
        Array.Clear(_places);
    }
}
