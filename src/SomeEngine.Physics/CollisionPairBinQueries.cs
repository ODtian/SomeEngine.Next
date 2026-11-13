using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public static partial class CollisionPairQueries
{
    public static void FillPairBins<TEnumerable, TPartition, TClassifier>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBins<TPartition> bins, TClassifier classifier)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => Fill(query, bins, classifier, false);
    public static void FillPairBinsParallel<TEnumerable, TPartition, TClassifier>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBins<TPartition> bins, TClassifier classifier)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => Fill(query, bins, classifier, true);
    private static void Fill<TEnumerable, TPartition, TClassifier>(RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBins<TPartition> bins, TClassifier classifier, bool parallel)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier
    {
        var fill = new RelationQuery<BinFill<TEnumerable, TPartition, TClassifier>, (Entity, Entity)>(new(query.Enumerable, bins, classifier, parallel));
        var output = new PairCounter(); fill.Enumerate(ref output);
    }
    private struct PairCounter : IRelationOutput<(Entity, Entity)> { public bool Add(in (Entity, Entity) pair) => true; }
    private readonly struct BinFill<TEnumerable, TPartition, TClassifier>(TEnumerable source, CollisionPairBins<TPartition> bins,
        TClassifier classifier, bool parallel) : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;
        public Query GetQuery(int position) => source.GetQuery(position);
        public QueryDescription AddReads(QueryDescription reads, int position) => classifier.AddReads(source.AddReads(reads, position));
        public bool Enumerate<TOutput>(scoped RelationQueryViews views, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        { bins.Fill(source, classifier, views, parallel); return true; }
    }
}
