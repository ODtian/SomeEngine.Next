using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

internal readonly ref struct KeyGroups<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly World _world;
    private readonly HashSet<TKey> _keys;
    private readonly List<Entity> _missing;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KeyGroups(World world, QueryCursor members)
    {
        _world = world;
        _keys = new HashSet<TKey>();
        _missing = new List<Entity>();
        foreach (var member in members.Rows)
        {
            if (member.Has<TComponent>())
                _keys.Add(member.ReadRef<TComponent>().GetKey());
            else
                _missing.Add(member.Entity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KeyGroups(World world, QueryCursor members, QueryCursor access)
    {
        _world = world;
        _keys = new HashSet<TKey>();
        _missing = new List<Entity>();
        foreach (var member in members.Rows)
        {
            if (access.TryGetRow(member.Entity, out var row) && row.Has<TComponent>())
                _keys.Add(row.ReadRef<TComponent>().GetKey());
            else
                _missing.Add(member.Entity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new(_world, _keys.GetEnumerator(), CollectionsMarshal.AsSpan(_missing));

    public ref struct Enumerator
    {
        private readonly World _world;
        private HashSet<TKey>.Enumerator _keys;
        private ReadOnlySpan<Entity> _missing;

        public Enumerator(World world, HashSet<TKey>.Enumerator keys, ReadOnlySpan<Entity> missing)
        {
            _world = world;
            _keys = keys;
            _missing = missing;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext(out KeyGroup<TKey> group)
        {
            if (_keys.MoveNext())
            {
                TKey key = _keys.Current;
                group = new KeyGroup<TKey>(key, _world.GetByIndex<TComponent, TKey>(key));
                return true;
            }
            if (_missing.IsEmpty)
            {
                group = default;
                return false;
            }
            group = new KeyGroup<TKey>(_missing);
            _missing = default;
            return true;
        }
    }
}

internal readonly ref struct KeyGroup<TKey>
{
    public KeyGroup(TKey key, ReadOnlySpan<Entity> entities)
    {
        Key = key;
        Entities = entities;
        HasKey = true;
    }

    public KeyGroup(ReadOnlySpan<Entity> entities)
    {
        Key = default!;
        Entities = entities;
        HasKey = false;
    }

    public readonly TKey Key;
    public ReadOnlySpan<Entity> Entities { get; }
    public bool HasKey { get; }
}
