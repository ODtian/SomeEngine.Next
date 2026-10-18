using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

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

    public QueryHandle GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        _enumerable.AddReads(definition, position).WithOptionalRead<TComponent>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var relation = new KeyRelation<TRelation, TImplementation, TComponent, TKey>(queries.Access, _relation, _implementation);
        var filtered = new RelationFilterOutput<KeyRelation<TRelation, TImplementation, TComponent, TKey>, TOutput, (Entity, Entity)>(relation, output, _exclude);
        try
        {
            return _enumerable.Enumerate(queries, ref filtered);
        }
        finally
        {
            output = filtered.Output;
        }
    }
}

public ref struct KeyRelation<TRelation, TImplementation, TComponent, TKey> : IQueryRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly QueryCursor _query;
    private TRelation _relation;
    private TImplementation _implementation;

    public KeyRelation(QueryCursor query, TRelation relation, TImplementation implementation)
    {
        _query = query;
        _relation = relation;
        _implementation = implementation;
    }

    public bool Matches(in (Entity, Entity) pair)
    {
        return _query.TryGetRow(pair.Item1, out var first) && _query.TryGetRow(pair.Item2, out var second)
            && Matches(in pair, in first, in second);
    }

    public bool Matches(in (Entity, Entity) pair, scoped in QueryRow first, scoped in QueryRow second)
    {
        bool matches = false;
        if (first.TryReadRef<TComponent>(out var firstValue) && second.TryReadRef<TComponent>(out var secondValue))
        {
            TKey firstKey = firstValue.Value.GetKey();
            TKey secondKey = secondValue.Value.GetKey();
            matches = _implementation.Matches(in _relation, in firstKey, in secondKey);
        }
        return matches;
    }
}
