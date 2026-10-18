using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics.Tests;

public sealed class BenchmarkSpatialFixtureTests
{
    [Fact]
    public void OneWayHandwrittenGroupsReturnTheTwoAscendingPermissionPairs()
    {
        using var world = new World();
        var first = world.CreateEntity(new Body { Bounds = Box(0.8f), CollisionCategories = new(1, 2) });
        var second = world.CreateEntity(new Body { Bounds = Box(0.4f), CollisionCategories = new(2, 0) });
        var third = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(2, 1) });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(OneWayCategories), default(OneWayCategoryBitmap));
        contacts.RebuildSummaries();
        var output = new SpatialPairOutput(new (Entity, Entity)[3]);
        var pairs = new (Entity, Entity)[3];
        var handwritten = new SpatialPairOutput(pairs);

        contacts.Enumerate(ref output);
        HandwrittenSpatialBitmap.OneWay(world, bodies, tree, contacts.Enumerable.Filter.Summary.Nodes, ref handwritten);

        Assert.Equal(2, output.Count);
        Assert.Equal([(first, second), (first, third)], pairs.AsSpan(0, handwritten.Count).ToArray()
            .OrderBy(pair => pair.Item2.Index));
        world.ReleaseQuery(bodies);
    }

    private static CollisionBounds Box(float x) => new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));
}
