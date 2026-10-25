using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public static partial class CollisionPairQueries
{
    public static void FillPairBins<TEnumerable, TPartition, TClassifier>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        TClassifier classifier, scoped RelationQueryViews views)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => bins.Fill(query.Enumerable, classifier, views, false);

    public static void FillPairBins<TEnumerable, TPartition, TClassifier>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        TClassifier classifier)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => FillBins(query, bins, classifier, false, null);

    public static void FillPairBinsParallel<TEnumerable, TPartition, TClassifier>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        TClassifier classifier)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => FillBins(query, bins, classifier, true, null);

    public static void FillPairBins<TEnumerable, TPartition, TClassifier>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, uint lastSystemVersion,
        CollisionPairBins<TPartition> bins, TClassifier classifier)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => FillBins(query, bins, classifier, false, lastSystemVersion);

    public static void FillPairBinsParallel<TEnumerable, TPartition, TClassifier>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, uint lastSystemVersion,
        CollisionPairBins<TPartition> bins, TClassifier classifier)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier => FillBins(query, bins, classifier, true, lastSystemVersion);

    private static void FillBins<TEnumerable, TPartition, TClassifier>(
        RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        TClassifier classifier, bool parallel, uint? lastSystemVersion)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier
    {
        var fill = new RelationQuery<BinFill<TEnumerable, TPartition, TClassifier>, (Entity, Entity)>(
            new(query.Enumerable, bins, classifier, parallel));
        var output = new PairCount();
        if (lastSystemVersion is { } baseline)
            fill.Enumerate(baseline, ref output);
        else
            fill.Enumerate(ref output);
    }

    private readonly struct BinFill<TEnumerable, TPartition, TClassifier>(TEnumerable source,
        CollisionPairBins<TPartition> bins, TClassifier classifier, bool parallel)
        : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;
        public QueryHandle GetQuery(int position) => source.GetQuery(position);
        public QueryDefinition AddReads(QueryDefinition definition, int position) =>
            classifier.AddReads(source.AddReads(definition, position));

        public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        {
            bins.Fill(source, classifier, queries, parallel);
            return true;
        }
    }

    public static void DetectPairs<TEnumerable, TPartition, TDetection>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        int bin, TDetection detection)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairDetection
    {
        var detect = new RelationQuery<BinDetection<TEnumerable, TPartition, TDetection>, (Entity, Entity)>(
            new(query.Enumerable, bins, bin, detection));
        var output = new PairCount();
        detect.Enumerate(ref output);
    }

    private readonly struct BinDetection<TEnumerable, TPartition, TDetection>(TEnumerable source,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection)
        : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairDetection
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;
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
                kernel.Execute(in pair, first, second);
            }
            return true;
        }
    }
}
