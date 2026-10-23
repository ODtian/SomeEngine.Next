using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class SpatialFilterTests
{
    [Fact]
    public void UniformCharacterGroupExclusionReturnsAnEmptyContactSet()
    {
        using var world = new World();
        var character = world.CreateEntity();
        var first = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var second = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var third = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f) });
        world.Add(first, new Hair { Character = character });
        world.Add(second, new Hair { Character = character });
        world.Add(third, new Hair { Character = character });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        contacts.RebuildSummaries();
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        contacts.Enumerate(ref output);

        Assert.Empty(pairs);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void DifferentCharacterGroupsReturnTheFourCrossCharacterPairs()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var b1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f) });
        var b2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b1, new Hair { Character = characterB });
        world.Add(b2, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        Collision.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(DefaultContacts))
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Enumerate(ref output);

        Assert.Equal([(a1, b1), (a1, b2), (a2, b1), (a2, b2)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void MissingHairAndMixedKeysRetainOrdinaryBodyContacts()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var ordinary = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        Collision.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Where(default(DefaultContacts))
            .Enumerate(ref output);

        Assert.Equal([(a1, ordinary), (a1, b), (a2, ordinary), (a2, b), (ordinary, b)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void OneWayGroupsFollowTheAscendingEntityPositions()
    {
        using var world = new World();
        var first = world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 2),
        });
        var second = world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(2, 0),
        });
        var third = world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(2, 1),
        });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(OneWayCategories), default(OneWayCategoryBitmap));
        contacts.RebuildSummaries();
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        contacts.Enumerate(ref output);

        Assert.Equal([(first, second), (first, third)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void FullSpatialRulesAndHandwrittenGroupsReturnTheFourContactPairs()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 3) });
        var s = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f), CollisionCategories = new(2, 1) });
        world.CreateEntity(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f), CollisionCategories = new(2, 2) });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f), CollisionCategories = new(1, 3) });
        var excluded = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.1f), CollisionCategories = new(1, 3) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        world.AddTag<ExcludedBody>(excluded);
        var allBodies = world.Query(world.QueryDefinition().All<Body>());
        var bodies = world.Query(world.QueryDefinition().All<Body>().None<ExcludedBody>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, allBodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        contacts.RebuildSummaries();
        var handwrittenTree = Collision.Build<Body, BodyBoundsReader>(world, allBodies, default);
        var keys = handwrittenTree.CreateKeySummary<Hair, Entity>();
        var categories = handwrittenTree.CreateBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission>(default, default);
        HandwrittenSpatialCollision.RebuildFilteredSummaries(world, bodies, keys, categories);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        var handwrittenPairs = new List<(Entity, Entity)>();
        var handwrittenOutput = new PairList(handwrittenPairs);

        contacts.Enumerate(ref output);
        HandwrittenSpatialCollision.Filtered(world, bodies, handwrittenTree, keys, categories, ref handwrittenOutput);

        (Entity, Entity)[] expected = [(a1, b), (a2, b), (b, s), (s, d)];
        Assert.Equal(expected, Ordered(pairs));
        Assert.Equal(expected, Ordered(handwrittenPairs));
        Assert.Equal(4, contacts.Count());
        Assert.True(contacts.Any());
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(allBodies);
    }

    [Fact]
    public void CategoryExclusionForwardsTheOneWayOnlyPairToSpanOutput()
    {
        using var world = new World();
        var first = world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 2),
        });
        var second = world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(2, 0),
        });
        world.CreateEntity(new Body
        {
            Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(2, 1),
        });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        Span<(Entity, Entity)> result = stackalloc (Entity, Entity)[1];
        var output = new FirstPair(result);

        bool completed = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(OneWayCategories), default(OneWayCategoryBitmap))
            .Except(default(MutualCategories), default(MutualCategoryBitmap))
            .Where(default(DefaultContacts))
            .Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Equal((first, second), result[0]);
        world.ReleaseQuery(bodies);
    }

    private static CollisionBounds Box(float x) =>
        new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

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

    private ref struct FirstPair(Span<(Entity, Entity)> result) : IRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _result = result;
        public int Count { get; private set; }

        public bool Add(in (Entity, Entity) pair)
        {
            _result[Count++] = pair;
            return false;
        }
    }
}
