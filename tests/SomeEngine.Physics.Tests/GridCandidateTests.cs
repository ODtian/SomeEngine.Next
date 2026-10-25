using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class GridCandidateTests
{
    [Fact]
    public void UniformGridReturnsEachOverlapOnceAcrossSharedCells()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new CollisionBounds(new(-1, 0, 0), new(2, 2, 2)));
        Entity second = world.CreateEntity(new CollisionBounds(new(0, 0, 0), new(3, 2, 2)));
        Entity third = world.CreateEntity(new CollisionBounds(new(2.5f, 0, 0), new(4, 2, 2)));
        QueryHandle bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var grid = Collision.CreateUniformGrid(cellSize: 1);
        var query = grid.Candidates(world.Within(bodies));
        var pairs = new List<(Entity, Entity)>();
        var output = new GridPairList(pairs);

        query.Enumerate(ref output);

        Assert.Equal([(first, second), (second, third)], Ordered(pairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void UniformGridKeepsComponentKeyAndBitmapChangesAcrossTypeBinBatches()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity characterA = world.CreateEntity();
        Entity characterB = world.CreateEntity();
        Entity a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        Entity a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        Entity b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 3) });
        Entity s = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f), CollisionCategories = new(2, 1) });
        world.CreateEntity(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f), CollisionCategories = new(2, 2) });
        Entity d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f), CollisionCategories = new(1, 3) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        QueryHandle bodies = world.Query(world.QueryDefinition().All<Body>());
        var grid = Collision.CreateUniformGrid(cellSize: 0.5f);
        var query = grid.Candidates<Body, BodyBoundsReader>(world.Within(bodies), default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var bins = new CollisionPairBins<GridPairRange>(jobs, 4, 1);

        query.FillPairBinsParallel(bins, default(OneGridPairType));

        Assert.Equal([(a1, b), (a2, b), (b, s), (s, d)],
            Ordered(bins.ReadBin(0).ToArray().Select(pair => (pair.First, pair.Second))));

        world.Replace(b, new Hair { Character = characterA });
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(b, s), (s, d)],
            Ordered(bins.ReadBin(0).ToArray().Select(pair => (pair.First, pair.Second))));

        world.Replace(b, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 0) });
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(s, d)], bins.ReadBin(0).ToArray().Select(pair => (pair.First, pair.Second)));

        world.Remove<Body>(s);
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Empty(bins.ReadBin(0).ToArray());
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void HierarchicalGridUpdatesCrossLevelPairsWhenBoundsAndMembershipChange()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new CollisionBounds(Vector3.Zero, new(0.2f)));
        Entity large = world.CreateEntity(new CollisionBounds(new(-1, 0, 0), new(8, 1, 1)));
        Entity third = world.CreateEntity(new CollisionBounds(new(5, 0, 0), new(5.2f, 0.2f, 0.2f)));
        QueryHandle bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var grid = Collision.CreateHierarchicalGrid(cellSize: 0.25f, levelCount: 8);
        var query = grid.Candidates(world.Within(bodies));
        var pairs = new List<(Entity, Entity)>();
        var output = new GridPairList(pairs);

        query.Enumerate(ref output);
        Assert.Equal([(first, large), (large, third)], Ordered(pairs));

        world.Replace(large, new CollisionBounds(new(20, 0, 0), new(28, 1, 1)));
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Empty(pairs);

        world.Replace(first, new CollisionBounds(new(-1, 0, 0), new(24, 1, 1)));
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Equal([(first, large), (first, third)], Ordered(pairs));

        world.Remove<CollisionBounds>(large);
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Equal([(first, third)], pairs);
        world.DestroyEntity(third);
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Empty(pairs);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void UniformGridBetweenEmitsUnorderedPairsOnceForOverlappingSets()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        Entity shared = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        Entity third = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        world.Add(third, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<CollisionBounds>().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<CollisionBounds>().All<PairSecondGroup>());
        var grid = Collision.CreateUniformGrid(cellSize: 1);
        var pairs = new List<(Entity, Entity)>();
        var output = new GridPairList(pairs);

        grid.Candidates(world.Between(firstGroup, secondGroup)).Enumerate(ref output);

        Assert.Equal([(first, shared), (first, third), (shared, third)], Ordered(pairs));
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    [Fact]
    public void HierarchicalGridProductPreservesPositionsAndSelfPairsThroughDirectionalRules()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(1, 2) });
        Entity shared = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(2, 3) });
        Entity third = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(1, 2) });
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        world.Add(third, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<Body>().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<Body>().All<PairSecondGroup>());
        var grid = Collision.CreateHierarchicalGrid(cellSize: 0.25f, levelCount: 4);
        var query = grid.Candidates<Body, BodyBoundsReader>(world.Product(secondGroup, firstGroup), default)
            .Where(default(OneWayCategories), default(OneWayCategoryBitmap));
        using var bins = new CollisionPairBins<GridPairRange>(jobs, 3, 1);

        query.FillPairBinsParallel(bins, default(OneGridPairType));

        Assert.Equal([(shared, first), (shared, shared), (third, shared)],
            Ordered(bins.ReadBin(0).ToArray().Select(pair => (pair.First, pair.Second))));
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    private static CollisionBounds Box(float x) => new(new(x, 0, 0), new(x + 1, 1, 1));

    private static (Entity, Entity)[] Ordered(IEnumerable<(Entity, Entity)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Index).ThenBy(pair => pair.Item2.Index).ToArray();

    private readonly struct GridPairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}

public readonly struct OneGridPairType : ICollisionPairClassifier
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition;
    public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
}
