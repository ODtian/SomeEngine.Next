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
    public void BuildJoinsIndependentBoundsReadsAndKeepsTheTreeUsableForMaintenance()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        using var reads = new CountdownEvent(2);
        Entity first = world.CreateEntity(Box(0));
        for (int index = 1; index < 32; index++) world.CreateEntity(Box(index * 3));
        Entity second = world.CreateEntity(Box(.2f));
        for (int index = 33; index < 63; index++) world.CreateEntity(Box(index * 3));
        Entity moved = world.CreateEntity(Box(189));
        var bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        try
        {
            var reader = new CooperatingBoundsReader(reads);
            CollisionTree tree = Collision.Build<CollisionBounds, CooperatingBoundsReader>(world, bodies, reader);
            var candidates = Collision.Candidates<CollisionBounds, CooperatingBoundsReader>(world, bodies, tree, reader);
            var pairs = new List<(Entity, Entity)>();
            var output = new PairList(pairs);
            candidates.Enumerate(ref output);
            Assert.Equal([(first, second)], Ordered(pairs));

            using var maintenance = candidates.CreateMaintenance(jobs);
            world.AcquireSystemVersion();
            world.Replace(moved, Box(.4f));
            maintenance.Schedule().Complete();
            pairs.Clear();
            candidates.Enumerate(ref output);
            Assert.Equal([(first, second), (first, moved), (second, moved)], Ordered(pairs));
        }
        finally
        {
            world.ReleaseQuery(bodies);
        }
    }

    [Fact]
    public void SparseBodiesReturnTheThreeNearbyPairs()
    {
        using var world = new World();
        var first = world.CreateEntity(Box(0));
        var second = world.CreateEntity(Box(0.75f));
        var third = world.CreateEntity(Box(0, 8));
        var fourth = world.CreateEntity(Box(0.75f, 8));
        var fifth = world.CreateEntity(Box(0, 0, 20));
        var sixth = world.CreateEntity(Box(0.75f, 0, 20));
        var bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        Collision.Candidates(world, bodies).Enumerate(ref output);

        Assert.Equal([(first, second), (third, fourth), (fifth, sixth)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void UniformBodiesUseTheCallerHeldTreeToReturnAdjacentPairs()
    {
        using var world = new World();
        var a = world.CreateEntity(Box(0));
        var b = world.CreateEntity(Box(0.75f));
        var c = world.CreateEntity(Box(1.5f));
        var d = world.CreateEntity(Box(0, 0.75f));
        var e = world.CreateEntity(Box(0.75f, 0.75f));
        var f = world.CreateEntity(Box(1.5f, 0.75f));
        var bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        CollisionTree tree = Collision.Build(world, bodies);
        var candidates = Collision.Candidates(world, bodies, tree);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        candidates.Enumerate(ref output);

        Assert.Equal([
            (a, b), (a, d), (a, e),
            (b, c), (b, d), (b, e), (b, f),
            (c, e), (c, f), (d, e), (e, f)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void DenseBodiesReturnEveryAscendingPairOnce()
    {
        using var world = new World();
        var a = world.CreateEntity(Box(0));
        var b = world.CreateEntity(Box(0.1f));
        var c = world.CreateEntity(Box(0.2f));
        var d = world.CreateEntity(Box(0.3f));
        var e = world.CreateEntity(Box(0.4f));
        var f = world.CreateEntity(Box(0.5f));
        var bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        Collision.Candidates(world, bodies).Enumerate(ref output);

        Assert.Equal([
            (a, b), (a, c), (a, d), (a, e), (a, f),
            (b, c), (b, d), (b, e), (b, f),
            (c, d), (c, e), (c, f), (d, e), (d, f), (e, f)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ScopedBodyAndHairRulesReturnTheFiveSpatialContactPairs()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f) });
        var s = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f) });
        var k = world.CreateEntity(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f) });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f) });
        var excluded = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.1f) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        world.AddTag<ExcludedBody>(excluded);
        var allBodies = world.Query(world.QueryDefinition().All<Body>());
        var bodies = world.Query(world.QueryDefinition().All<Body>().None<ExcludedBody>());
        CollisionTree tree = Collision.Build<Body, BodyBoundsReader>(world, allBodies, default);
        CollisionTree handwrittenTree = Collision.Build<Body, BodyBoundsReader>(world, allBodies, default);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        var handwrittenPairs = new List<(Entity, Entity)>();
        var handwrittenOutput = new PairList(handwrittenPairs);

        Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Except(default(SameCharacterHair))
            .Enumerate(ref output);
        HandwrittenSpatialCollision.Enumerate(world, bodies, handwrittenTree, ref handwrittenOutput);

        (Entity, Entity)[] expected = [(a1, b), (a2, b), (b, s), (s, d), (k, d)];
        Assert.Equal(expected, Ordered(pairs));
        Assert.Equal(expected, Ordered(handwrittenPairs));
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(allBodies);
    }

    private static CollisionBounds Box(float x, float y = 0, float z = 0) =>
        new(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));

    private readonly struct CooperatingBoundsReader(CountdownEvent reads) : ICollisionBoundsReader<CollisionBounds>
    {
        public CollisionBounds Read(in CollisionBounds component)
        {
            if (component.Min.X < 1 && reads.CurrentCount > 0)
            {
                reads.Signal();
                if (!reads.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return component;
        }
    }

    private static (Entity, Entity)[] Ordered(List<(Entity, Entity)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Index).ThenBy(pair => pair.Item1.Generation)
            .ThenBy(pair => pair.Item2.Index).ThenBy(pair => pair.Item2.Generation).ToArray();

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}
