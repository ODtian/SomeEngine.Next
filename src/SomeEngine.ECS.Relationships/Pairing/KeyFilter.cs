using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public readonly struct KeyFilter<TEnumerable, TRelation, TImplementation, TComponent, TKey>
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly TEnumerable _enumerable;
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    private readonly bool _exclude;

    public KeyFilter(TEnumerable enumerable, TRelation relation, TImplementation implementation, bool exclude)
    {
        _enumerable = enumerable;
        _relation = relation;
        _implementation = implementation;
        _exclude = exclude;
    }

    public World World => _enumerable.World;

    public int QueryCount => _enumerable.QueryCount;

    public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;

    public Query GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        _enumerable.AddReads(definition, position).Optional<TComponent>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        => _exclude ? Enumerate<ExceptSelection, TOutput>(queries, ref output)
            : Enumerate<WhereSelection, TOutput>(queries, ref output);

    private bool Enumerate<TSelection, TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TSelection : struct, IRelationSelection
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using var relation = new KeyRelation<TRelation, TImplementation, TComponent, TKey>(queries.World, _relation, _implementation);
        var filtered = new RelationFilterOutput<KeyRelation<TRelation, TImplementation, TComponent, TKey>, TOutput, (Entity, Entity), TSelection>(relation, new(ref output));
        return _enumerable.Enumerate(queries, ref filtered);
    }
}

public ref struct KeyRelation<TRelation, TImplementation, TComponent, TKey> : IQueryRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private ReadLookup<TComponent> _values;
    private ReadOnlySpan<TComponent> _firstValues, _secondValues;
    private QueryChunk _firstChunk, _secondChunk;
    private TRelation _relation;
    private TImplementation _implementation;
    public KeyRelation(World world, TRelation relation, TImplementation implementation)
    {
        this = default;
        _values = world.OpenRead<TComponent>();
        _relation = relation; _implementation = implementation;
    }
    public void Dispose() => _values.Dispose();
    public void Bind(QueryChunk first, QueryChunk second)
    {
        _firstChunk = first; _secondChunk = second;
        first.TryRead<TComponent>(out _firstValues); second.TryRead<TComponent>(out _secondValues);
    }
    public bool Matches(in (Entity, Entity) pair)
    {
        if (!_values.Has(pair.Item1) || !_values.Has(pair.Item2)) return false;
        TKey first = _values.ReadKnown(pair.Item1).GetKey(), second = _values.ReadKnown(pair.Item2).GetKey();
        return _implementation.Matches(in _relation, in first, in second);
    }
    public bool Matches(in (Entity, Entity) pair, int first, int second, bool reversed)
    {
        if (_firstValues.IsEmpty || _secondValues.IsEmpty || !_firstChunk.Has<TComponent>(first) || !_secondChunk.Has<TComponent>(second)) return false;
        TKey firstKey = _firstValues[first].GetKey(), secondKey = _secondValues[second].GetKey();
        return reversed ? _implementation.Matches(in _relation, in secondKey, in firstKey)
            : _implementation.Matches(in _relation, in firstKey, in secondKey);
    }
}
