using SomeEngine.ECS.Spatial;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CollisionCandidatesTests
{
    [Fact]
    public void SparseBodiesReturnTheThreeNearbyPairs()
    {
        using var world = new World();
        var first = world.Create(Box(0));
        var second = world.Create(Box(0.75f));
        var third = world.Create(Box(0, 8));
        var fourth = world.Create(Box(0.75f, 8));
        var fifth = world.Create(Box(0, 0, 20));
        var sixth = world.Create(Box(0.75f, 0, 20));
        var bodies = world.Query(new QueryDescription().WithStored<SpatialBounds>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        SpatialQueries.Candidates(world, bodies).Enumerate(ref output);

        Assert.Equal([(first, second), (third, fourth), (fifth, sixth)], Ordered(pairs));

    }

    [Fact]
    public void UniformBodiesUseTheCallerHeldTreeToReturnAdjacentPairs()
    {
        using var world = new World();
        var a = world.Create(Box(0));
        var b = world.Create(Box(0.75f));
        var c = world.Create(Box(1.5f));
        var d = world.Create(Box(0, 0.75f));
        var e = world.Create(Box(0.75f, 0.75f));
        var f = world.Create(Box(1.5f, 0.75f));
        var bodies = world.Query(new QueryDescription().WithStored<SpatialBounds>());
        BoundsTree tree = SpatialQueries.Build(world, bodies);
        var candidates = SpatialQueries.Candidates(world, bodies, tree);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        candidates.Enumerate(ref output);

        Assert.Equal([
            (a, b), (a, d), (a, e),
            (b, c), (b, d), (b, e), (b, f),
            (c, e), (c, f), (d, e), (e, f)], Ordered(pairs));

    }

    [Fact]
    public void DenseBodiesReturnEveryAscendingPairOnce()
    {
        using var world = new World();
        var a = world.Create(Box(0));
        var b = world.Create(Box(0.1f));
        var c = world.Create(Box(0.2f));
        var d = world.Create(Box(0.3f));
        var e = world.Create(Box(0.4f));
        var f = world.Create(Box(0.5f));
        var bodies = world.Query(new QueryDescription().WithStored<SpatialBounds>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        SpatialQueries.Candidates(world, bodies).Enumerate(ref output);

        Assert.Equal([
            (a, b), (a, c), (a, d), (a, e), (a, f),
            (b, c), (b, d), (b, e), (b, f),
            (c, d), (c, e), (c, f), (d, e), (d, f), (e, f)], Ordered(pairs));

    }

    [Fact]
    public void ScopedBodyAndHairRulesReturnTheFiveSpatialContactPairs()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var characterA = world.Create();
        var characterB = world.Create();
        var a1 = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var a2 = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f) });
        var s = world.Create(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f) });
        var k = world.Create(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f) });
        var d = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f) });
        var excluded = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.1f) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        world.Add(excluded, new ExcludedBody());
        var allBodies = world.Query(new QueryDescription().WithStored<Body>());
        var bodies = world.Query(new QueryDescription().WithStored<Body>().Without<ExcludedBody>());
        BoundsTree tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, allBodies, default);
        BoundsTree handwrittenTree = SpatialQueries.Build<Body, BodyBoundsReader>(world, allBodies, default);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        var handwrittenPairs = new List<(Entity, Entity)>();
        var handwrittenOutput = new PairList(handwrittenPairs);

        SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Except(default(SameCharacterHair))
            .Enumerate(ref output);
        HandwrittenSpatialCollision.Enumerate(world, bodies, handwrittenTree, ref handwrittenOutput);

        (Entity, Entity)[] expected = [(a1, b), (a2, b), (b, s), (s, d), (k, d)];
        Assert.Equal(expected, Ordered(pairs));
        Assert.Equal(expected, Ordered(handwrittenPairs));


    }

    private static SpatialBounds Box(float x, float y = 0, float z = 0) =>
        new(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));

    private static (Entity, Entity)[] Ordered(List<(Entity, Entity)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Id).ThenBy(pair => pair.Item1.Generation)
            .ThenBy(pair => pair.Item2.Id).ThenBy(pair => pair.Item2.Generation).ToArray();

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}
