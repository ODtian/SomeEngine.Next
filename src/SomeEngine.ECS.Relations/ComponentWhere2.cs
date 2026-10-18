using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct ComponentWhere<TEnumerable, TRelation, TFirst, TSecond> : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    private readonly TEnumerable _enumerable;
    private readonly TRelation _relation;

    public ComponentWhere(TEnumerable enumerable, TRelation relation)
    {
        _enumerable = enumerable;
        _relation = relation;
    }

    public World World => _enumerable.World;

    public int QueryCount => _enumerable.QueryCount;

    public QueryHandle GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position)
    {
        definition = _enumerable.AddReads(definition, position);
        if (position == 0)
            definition = definition.WithOptionalRead<TFirst>();
        if (position == 1 || QueryCount == 1)
            definition = definition.WithOptionalRead<TSecond>();
        return definition;
    }

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var relation = new ComponentRelation<TRelation, TFirst, TSecond>(queries.Access, _relation);
        var filtered = new RelationFilterOutput<ComponentRelation<TRelation, TFirst, TSecond>, TOutput, (Entity, Entity)>(relation, output);
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
