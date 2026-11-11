using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityPairsBetween, (Entity, Entity)> Between(this World world, Query first, Query second) =>
        new(new EntityPairsBetween(world, first, second));
}

public readonly struct EntityPairsBetween(World world, Query first, Query second) : IQueryRelationEnumerable<(Entity, Entity)>
{
    public World World => world;
    public int QueryCount => 2;
    public Query GetQuery(int position) => position == 0 ? first : second;
    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        foreach (QueryChunk firstChunk in queries.First)
            foreach (QueryChunk secondChunk in queries.Second)
            {
                if (TOutput.UsesBoundColumns) output.Bind(firstChunk, secondChunk);
                for (int firstRow = 0; firstRow < firstChunk.Count; firstRow++)
                    for (int secondRow = 0; secondRow < secondChunk.Count; secondRow++)
                    {
                        Entity firstEntity = firstChunk.Entities[firstRow], secondEntity = secondChunk.Entities[secondRow];
                        int order = EntityOrder.Compare(firstEntity, secondEntity);
                        if (order == 0 || order > 0 && queries.First.Contains(secondEntity) && queries.Second.Contains(firstEntity)) continue;
                        var pair = order < 0 ? (firstEntity, secondEntity) : (secondEntity, firstEntity);
                        if (!(TOutput.UsesBoundColumns ? output.Add(in pair, firstRow, secondRow, order > 0) : output.Add(in pair))) return false;
                    }
            }
        return true;
    }
}
