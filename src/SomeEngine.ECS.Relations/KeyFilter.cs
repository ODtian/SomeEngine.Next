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

    public QueryHandle GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        _enumerable.AddReads(definition, position).WithOptionalRead<TComponent>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var relation = new KeyRelation<TRelation, TImplementation, TComponent, TKey>(queries.Access, _relation, _implementation, _exclude);
        var filtered = new RelationFilterOutput<KeyRelation<TRelation, TImplementation, TComponent, TKey>, TOutput, (Entity, Entity)>(relation, output);
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

public ref struct KeyRelation<TRelation, TImplementation, TComponent, TKey> : IRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly QueryCursor _query;
    private TRelation _relation;
    private TImplementation _implementation;
    private readonly bool _exclude;

    public KeyRelation(QueryCursor query, TRelation relation, TImplementation implementation, bool exclude)
    {
        _query = query;
        _relation = relation;
        _implementation = implementation;
        _exclude = exclude;
    }

    public bool Matches(in (Entity, Entity) pair)
    {
        bool matches = false;
        if (_query.TryGetRow(pair.Item1, out var first) && first.Has<TComponent>() &&
            _query.TryGetRow(pair.Item2, out var second) && second.Has<TComponent>())
        {
            TKey firstKey = first.ReadRef<TComponent>().GetKey();
            TKey secondKey = second.ReadRef<TComponent>().GetKey();
            matches = _implementation.Matches(in _relation, in firstKey, in secondKey);
        }
        return matches != _exclude;
    }
}
