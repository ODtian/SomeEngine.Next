using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class IntervalCandidateTests
{
    [Fact]
    public void SapCandidatesDeliverOverlappingEntitiesToConcreteDetection()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new CollisionBounds(new(0, 0, 0), new(2, 1, 1)));
        Entity second = world.CreateEntity(new CollisionBounds(new(1, 0, 0), new(3, 1, 1)));
        Entity third = world.CreateEntity(new CollisionBounds(new(2.5f, 0, 0), new(4, 1, 1)));
        Entity separated = world.CreateEntity(new CollisionBounds(new(0, 5, 0), new(3, 6, 1)));
        world.Add(first, new PairGeometry(0, 11, 2));
        world.Add(second, new PairGeometry(1, 22, 3));
        world.Add(third, new PairGeometry(0, 33, 5));
        world.Add(separated, new PairGeometry(1, 44, 7));
        QueryHandle bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var intervals = Collision.CreateSap(CollisionAxis.X);
        var query = intervals.Candidates(world.Within(bodies));
        using var bins = new CollisionPairBins<IntervalPairRange>(jobs, 3, 3);
        var results = new List<DetectedPair>();

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));
        query.DetectPairs(bins, 1, new SumGeometrySizes(results));

        Assert.Equal([
            new DetectedPair(first, second, 11, 22, 5),
            new DetectedPair(second, third, 22, 33, 8)
        ], results);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void PartitionedSapMergesEachCrossPartitionPairOnceIntoTypeBins()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new CollisionBounds(new(0, 0, -2), new(1, 1, 2)));
        Entity second = world.CreateEntity(new CollisionBounds(new(0, 0, -0.5f), new(1, 1, 3)));
        Entity third = world.CreateEntity(new CollisionBounds(new(0, 0, 2.5f), new(1, 1, 4)));
        world.CreateEntity(new CollisionBounds(new(0, 0, 8), new(1, 1, 9)));
        world.Add(first, new PairGeometry(0, 11, 2));
        world.Add(second, new PairGeometry(1, 22, 3));
        world.Add(third, new PairGeometry(0, 33, 5));
        QueryHandle bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var intervals = Collision.CreatePartitionedSap(CollisionAxis.Z, partitionSize: 1);
        var query = intervals.Candidates(world.Within(bodies));
        using var bins = new CollisionPairBins<IntervalPairRange>(jobs, 2, 3);
        var results = new List<DetectedPair>();

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));
        query.DetectPairs(bins, 1, new SumGeometrySizes(results));

        Assert.Equal([
            new DetectedPair(first, second, 11, 22, 5),
            new DetectedPair(second, third, 22, 33, 8)
        ], results);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void PartitionedSapKeepsMotionMembershipAndRuleChangesAcrossBatches()
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
        var intervals = Collision.CreatePartitionedSap(CollisionAxis.X, partitionSize: 0.5f);
        var query = intervals.Candidates<Body, BodyBoundsReader>(world.Within(bodies), default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var bins = new CollisionPairBins<IntervalPairRange>(jobs, 4, 1);

        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(a1, b), (a2, b), (b, s), (s, d)], ReadPairs(bins));

        world.Replace(b, new Hair { Character = characterA });
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(b, s), (s, d)], ReadPairs(bins));

        world.Replace(b, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 0) });
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(s, d)], ReadPairs(bins));

        world.Replace(s, new Body { Kind = MotionKind.Static, Bounds = Box(8), CollisionCategories = new(2, 1) });
        world.Replace(d, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(1, 3) });
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(a1, d), (a2, d)], ReadPairs(bins));

        world.Remove<Body>(a2);
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Equal([(a1, d)], ReadPairs(bins));
        world.DestroyEntity(d);
        query.FillPairBinsParallel(bins, default(OneGridPairType));
        Assert.Empty(bins.ReadBin(0).ToArray());
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void SapBetweenUpdatesOverlappingSetsWhenMembersAndBoundsChange()
    {
        using var world = new World();
        Entity first = world.CreateEntity(Box(0));
        Entity shared = world.CreateEntity(Box(0));
        Entity third = world.CreateEntity(Box(0));
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        world.Add(third, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<CollisionBounds>().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<CollisionBounds>().All<PairSecondGroup>());
        var intervals = Collision.CreateSap(CollisionAxis.Y);
        var query = intervals.Candidates(world.Between(firstGroup, secondGroup));
        var pairs = new List<(Entity, Entity)>();
        var output = new IntervalPairList(pairs);

        query.Enumerate(ref output);
        Assert.Equal([(first, shared), (first, third), (shared, third)], pairs);

        world.Remove<PairSecondGroup>(shared);
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Equal([(first, third), (shared, third)], pairs);

        world.Replace(third, new CollisionBounds(new(0, 10, 0), new(1, 11, 1)));
        pairs.Clear();
        query.Enumerate(ref output);
        Assert.Empty(pairs);
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    [Fact]
    public void PartitionedSapProductKeepsPositionsAndSelfPairsThroughDirectionalRules()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(1, 2) });
        Entity shared = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(2, 3) });
        Entity third = world.CreateEntity(new Body { Bounds = Box(0), CollisionCategories = new(1, 2) });
        world.Add(first, new PairGeometry(0, 11, 2));
        world.Add(shared, new PairGeometry(0, 22, 3));
        world.Add(third, new PairGeometry(0, 33, 5));
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        world.Add(third, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<Body>().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<Body>().All<PairSecondGroup>());
        var intervals = Collision.CreatePartitionedSap(CollisionAxis.Y, partitionSize: 0.25f);
        var query = intervals.Candidates<Body, BodyBoundsReader>(world.Product(secondGroup, firstGroup), default)
            .Where(default(OneWayCategories), default(OneWayCategoryBitmap));
        using var bins = new CollisionPairBins<IntervalPairRange>(jobs, 3, 1);

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));

        Assert.Equal([
            new CollisionGeometryPair(shared, first, 22, 11),
            new CollisionGeometryPair(shared, shared, 22, 22),
            new CollisionGeometryPair(third, shared, 33, 22)
        ], bins.ReadBin(0).ToArray());
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    private static CollisionBounds Box(float x) => new(new(x, 0, 0), new(x + 1, 1, 1));

    private static (Entity, Entity)[] ReadPairs(CollisionPairBins<IntervalPairRange> bins) =>
        bins.ReadBin(0).ToArray().Select(pair => (pair.First, pair.Second))
            .OrderBy(pair => pair.First.Index).ThenBy(pair => pair.Second.Index).ToArray();

    private readonly struct IntervalPairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}
