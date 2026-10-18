using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct ComponentWhere<TEnumerable, TRelation, TComponent> : IQueryRelationEnumerable<Entity>
    where TEnumerable : struct, IQueryRelationEnumerable<Entity>
    where TRelation : struct, IRelation<TComponent>
    where TComponent : struct
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

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        _enumerable.AddReads(definition, position).WithOptionalRead<TComponent>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<Entity>, allows ref struct
    {
        var relation = new ComponentRelation<TRelation, TComponent>(queries.Access, _relation);
        var filtered = new RelationFilterOutput<ComponentRelation<TRelation, TComponent>, TOutput, Entity>(relation, output);
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
