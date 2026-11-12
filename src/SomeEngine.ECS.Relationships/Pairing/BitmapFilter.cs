using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public struct BitmapFilter<TEnumerable, TRelation, TImplementation, TComponent, TPermission, TSelection>
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
    where TSelection : struct, IRelationSelection
{
    private TEnumerable _enumerable;
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    public BitmapFilter(TEnumerable enumerable, TRelation relation, TImplementation implementation)
    {
        _enumerable = enumerable;
        _relation = relation;
        _implementation = implementation;
    }

    public World World => _enumerable.World;

    public int QueryCount => _enumerable.QueryCount;

    public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;

    public Query GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        _enumerable.AddReads(definition, position).Optional<TComponent>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using var relation = new BitmapRelation<TRelation, TImplementation, TComponent, TPermission>(queries.World, _relation, _implementation);
        var filtered = new BitmapFilterOutput<TRelation, TImplementation, TComponent, TPermission, TOutput, TSelection>
        {
            Relation = relation,
            Output = new(ref output)
        };
        return _enumerable.Enumerate(queries, ref filtered);
    }
}
