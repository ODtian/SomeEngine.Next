using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public static partial class Collision
{
    public static RelationQuery<TreeCandidates<CollisionBounds, CollisionBoundsReader>, (Entity, Entity)>
        Candidates(World world, Query members, BoundsTree tree) =>
        SpatialQueries.Candidates<CollisionBounds, CollisionBoundsReader>(world, members, tree, default);

    public static RelationQuery<TreeCandidates<TGeometry, TReader>, (Entity, Entity)>
        Candidates<TGeometry, TReader>(World world, Query members, BoundsTree tree, TReader reader)
        where TGeometry : struct where TReader : struct, ISpatialBoundsReader<TGeometry> =>
        SpatialQueries.Candidates<TGeometry, TReader>(world, members, tree, reader);

    public static RelationQuery<TreeCandidates<TGeometry, TReader>, (Entity, Entity)>
        Candidates<TGeometry, TReader>(World world, Query members, TReader reader)
        where TGeometry : struct where TReader : struct, ISpatialBoundsReader<TGeometry> =>
        SpatialQueries.Candidates<TGeometry, TReader>(world, members, reader);
}
