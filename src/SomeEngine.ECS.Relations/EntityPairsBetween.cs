using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityPairsBetween, (Entity, Entity)> Between(
        this World world, QueryHandle first, QueryHandle second) => new(new EntityPairsBetween(world, first, second));
}

public readonly struct EntityPairsBetween : IQueryRelationEnumerable<(Entity, Entity)>
{
    private readonly QueryHandle _first;
    private readonly QueryHandle _second;

    public EntityPairsBetween(World world, QueryHandle first, QueryHandle second)
    {
        World = world;
        _first = first;
        _second = second;
    }

    public World World { get; }

    public int QueryCount => 2;

    public QueryHandle GetQuery(int position) => position == 0 ? _first : _second;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        foreach (var first in queries.First.Rows)
        {
            foreach (var second in queries.Second.Rows)
            {
                int order = EntityOrder.Compare(first.Entity, second.Entity);
                if (order == 0)
                    continue;
                if (order > 0 && queries.First.Contains(second.Entity) &&
                    queries.Second.Contains(first.Entity))
                    continue;
                var pair = order < 0 ? (first.Entity, second.Entity) : (second.Entity, first.Entity);
                if (!output.Add(in pair))
                    return false;
            }
        }
        return true;
    }
}
