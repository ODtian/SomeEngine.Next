using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal readonly ref struct KeyGroups<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly ValueIndex<TComponent, TKey> _index;
    private readonly HashSet<TKey> _keys;
    private readonly List<Entity> _missing;

    public KeyGroups(World world, QueryScope members)
    {
        _index = world.Index<TComponent, TKey>();
        _keys = new();
        _missing = new();
        foreach (QueryChunk chunk in members)
        {
            chunk.TryRead<TComponent>(out ReadOnlySpan<TComponent> values);
            for (int row = 0; row < chunk.Count; row++)
                if (!values.IsEmpty && chunk.Has<TComponent>(row)) _keys.Add(values[row].GetKey());
                else _missing.Add(chunk.Entities[row]);
        }
    }

    public Enumerator GetEnumerator() => new(_index, _keys.GetEnumerator(), CollectionsMarshal.AsSpan(_missing));

    public ref struct Enumerator
    {
        private readonly ValueIndex<TComponent, TKey> _index;
        private HashSet<TKey>.Enumerator _keys;
        private ReadOnlySpan<Entity> _missing;
        private IndexMembers _members;
        private bool _hasMembers;
        private KeyGroup<TKey> _current;

        internal Enumerator(ValueIndex<TComponent, TKey> index, HashSet<TKey>.Enumerator keys, ReadOnlySpan<Entity> missing)
        {
            this = default;
            _index = index;
            _keys = keys;
            _missing = missing;
        }
        public readonly KeyGroup<TKey> Current => _current;
        public bool MoveNext()
        {
            Dispose();
            if (_keys.MoveNext())
            {
                _members = _index.Find(_keys.Current);
                _hasMembers = true;
                _current = new(_keys.Current, _members.Entities);
                return true;
            }
            if (_missing.IsEmpty) return false;
            _current = new(_missing);
            _missing = default;
            return true;
        }
        public void Dispose()
        {
            if (_hasMembers) { _members.Dispose(); _hasMembers = false; }
        }
    }
}

internal readonly ref struct KeyGroup<TKey>
{
    public readonly TKey Key;
    public ReadOnlySpan<Entity> Entities { get; }
    public bool HasKey { get; }
    public KeyGroup(TKey key, ReadOnlySpan<Entity> entities) { Key = key; Entities = entities; HasKey = true; }
    public KeyGroup(ReadOnlySpan<Entity> entities) { Key = default!; Entities = entities; HasKey = false; }
}
