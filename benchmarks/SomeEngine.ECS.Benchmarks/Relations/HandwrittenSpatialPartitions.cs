using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using CategorySummary = SomeEngine.ECS.Spatial.BoundsBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public readonly struct HandwrittenSpatialPartitions(World world, Query bodies, BoundsTree tree,
    BoundsKeySummary<Hair, Entity> keys, CategorySummary categories) : ISpatialPartitionEnumerable<BoundsNodePair>
{
    public World World => world;
    public BoundsTree Tree => tree;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public Query GetQuery(int position) => bodies;
    public QueryDescription AddReads(QueryDescription definition, int position) =>
        definition.Optional<Body>().Optional<Hair>();
    public void Prepare(scoped RelationQueryViews queries) { }
    public int CreatePartitions(Span<BoundsNodePair> partitions) => tree.CreatePartitions(partitions);

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        Enumerate(new(tree.Root, tree.Root), queries, ref output);

    public bool Enumerate<TOutput>(BoundsNodePair partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        HandwrittenSpatialCollision.EnumeratePartition(partition, queries, tree, keys, categories, ref output);
}
