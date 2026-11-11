using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public readonly struct ComponentPairsWithin<TRelation, TFirst, TSecond, TSelection>(EntityPairsWithin source, TRelation relation)
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
    where TSelection : struct, IRelationSelection
{
    public World World => source.World;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription definition, int position) => definition.Optional<TFirst>().Optional<TSecond>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using var components = new ComponentRelation<TRelation, TFirst, TSecond>(queries.World, relation);
        var filtered = new RelationFilterOutput<ComponentRelation<TRelation, TFirst, TSecond>, TOutput, (Entity, Entity), TSelection>(components, new(ref output));
        return source.Enumerate(queries, ref filtered);
    }
}
