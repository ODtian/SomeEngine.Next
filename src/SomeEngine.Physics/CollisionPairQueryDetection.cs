using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>Reads the complete active query when geometry refers to additional simulated entities.</summary>
public interface ICollisionPairQueryDetection
{
    QueryDefinition AddReads(QueryDefinition definition);
    void Execute(in CollisionGeometryPair pair, scoped QueryCursor access, scoped QueryRow first, scoped QueryRow second);
}

public static partial class CollisionPairQueries
{
    public static void DetectPairsWithQuery<TEnumerable, TPartition, TDetection>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        int bin, TDetection detection)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        var detect = new RelationQuery<QueryBinDetection<TEnumerable, TPartition, TDetection>, (Entity, Entity)>(
            new(query.Enumerable, bins, bin, detection));
        var output = new PairCount();
        detect.Enumerate(ref output);
    }

    private readonly struct QueryBinDetection<TEnumerable, TPartition, TDetection>(TEnumerable source,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection)
        : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => false;
        public QueryHandle GetQuery(int position) => source.GetQuery(position);
        public QueryDefinition AddReads(QueryDefinition definition, int position) =>
            detection.AddReads(source.AddReads(definition, position));

        public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        {
            TDetection kernel = detection;
            foreach (ref readonly CollisionGeometryPair pair in bins.ReadBin(bin))
            {
                queries.Access.TryGetRow(pair.First, out QueryRow first);
                queries.Access.TryGetRow(pair.Second, out QueryRow second);
                kernel.Execute(in pair, queries.Access, first, second);
            }
            return true;
        }
    }
}
