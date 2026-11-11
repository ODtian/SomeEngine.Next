using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityPairsWithin, (Entity, Entity)> Within(this World world, Query members) =>
        new(new EntityPairsWithin(world, members));
}

public readonly struct EntityPairsWithin(World world, Query members) : IQueryRelationEnumerable<(Entity, Entity)>
{
    public World World => world;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public Query GetQuery(int position) => members;
    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        int firstChunkIndex = 0;
        foreach (QueryChunk firstChunk in queries.First)
        {
            int secondChunkIndex = 0;
            foreach (QueryChunk secondChunk in queries.First)
            {
                int index = secondChunkIndex++;
                if (index < firstChunkIndex) continue;
                if (TOutput.UsesBoundColumns) output.Bind(firstChunk, secondChunk);
                for (int first = 0; first < firstChunk.Count; first++)
                    for (int second = index == firstChunkIndex ? first + 1 : 0; second < secondChunk.Count; second++)
                    {
                        Entity firstEntity = firstChunk.Entities[first], secondEntity = secondChunk.Entities[second];
                        bool reversed = EntityOrder.Precedes(secondEntity, firstEntity);
                        var pair = reversed ? (secondEntity, firstEntity) : (firstEntity, secondEntity);
                        if (!(TOutput.UsesBoundColumns ? output.Add(in pair, first, second, reversed) : output.Add(in pair))) return false;
                    }
            }
            firstChunkIndex++;
        }
        return true;
    }
}
