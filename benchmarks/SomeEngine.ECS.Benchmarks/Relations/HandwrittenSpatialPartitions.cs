using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;
using CategorySummary = SomeEngine.Physics.CollisionBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public readonly struct HandwrittenSpatialPartitions(World world, QueryHandle bodies, CollisionTree tree,
    CollisionKeySummary<Hair, Entity> keys, CategorySummary categories) : ICollisionPartitionEnumerable
{
    public World World => world;
    public CollisionTree Tree => tree;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => bodies;
    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        definition.WithOptionalRead<Body>().WithOptionalRead<Hair>();
    public void Prepare(scoped RelationQueryViews queries) { }

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        Enumerate(new(tree.Root, tree.Root), queries, ref output);

    public bool Enumerate<TOutput>(CollisionNodePair partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        HandwrittenSpatialCollision.EnumeratePartition(partition, queries, tree, keys, categories, ref output);
}
