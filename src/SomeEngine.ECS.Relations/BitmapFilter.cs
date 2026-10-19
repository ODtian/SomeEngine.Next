using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct BitmapFilter<TEnumerable, TRelation, TImplementation, TComponent, TPermission>
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    private readonly TEnumerable _enumerable;
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    private readonly bool _exclude;

    public BitmapFilter(TEnumerable enumerable, TRelation relation, TImplementation implementation, bool exclude)
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
        => _exclude ? Enumerate<ExceptSelection, TOutput>(queries, ref output)
            : Enumerate<WhereSelection, TOutput>(queries, ref output);

    private bool Enumerate<TSelection, TOutput>(RelationQueryViews queries, ref TOutput output)
        where TSelection : struct, IRelationSelection
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        BitmapFilterOutput<TRelation, TImplementation, TComponent, TPermission, TOutput, TSelection> filtered;
        filtered.Relation = new(queries.Access, _relation, _implementation);
        filtered.Output = output;
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
