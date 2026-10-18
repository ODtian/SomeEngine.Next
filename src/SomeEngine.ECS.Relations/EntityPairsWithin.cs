using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityPairsWithin, (Entity, Entity)> Within(
        this World world, QueryHandle members) => new(new EntityPairsWithin(world, members));
}

public readonly struct EntityPairsWithin : IQueryRelationEnumerable<(Entity, Entity)>
{
    private readonly QueryHandle _members;

    public EntityPairsWithin(World world, QueryHandle members)
    {
        World = world;
        _members = members;
    }

    public World World { get; }

    public int QueryCount => 1;

    public static bool ReadsFromSingleQuery => true;

    public QueryHandle GetQuery(int position) => _members;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var firstRows = queries.First.Rows;
        while (firstRows.MoveNext(out var first))
        {
            var secondRows = firstRows;
            while (secondRows.MoveNext(out var second))
            {
                var pair = EntityOrder.OrderPair(first.Entity, second.Entity);
                bool continued = pair.Item1 == first.Entity ? output.Add(in pair, in first, in second) : output.Add(in pair, in second, in first);
                if (!continued)
                    return false;
            }
        }
        return true;
    }
}
