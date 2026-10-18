using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityProduct2, (Entity, Entity)> Product(
        this World world, QueryHandle first, QueryHandle second) => new(new EntityProduct2(world, first, second));
}

public readonly struct EntityProduct2 : IQueryRelationEnumerable<(Entity, Entity)>
{
    private readonly QueryHandle _first;
    private readonly QueryHandle _second;

    public EntityProduct2(World world, QueryHandle first, QueryHandle second)
    {
        World = world;
        _first = first;
        _second = second;
    }

    public World World { get; }

    public int QueryCount => 2;

    public QueryHandle GetQuery(int position) => position == 0 ? _first : _second;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        foreach (var first in queries.First.Rows)
        {
            foreach (var second in queries.Second.Rows)
            {
                var pair = (first.Entity, second.Entity);
                if (!output.Add(in pair))
                    return false;
            }
        }
        return true;
    }
}
