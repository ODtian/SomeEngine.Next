using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CollisionPairBinTests
{
    [Fact]
    public void SpatialTypeBinsDeliverMixedGeometryPairsToConcreteDetection()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity sphere = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        Entity box = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        Entity secondBox = world.CreateEntity(new CollisionBounds(Vector3.Zero, Vector3.One));
        world.Add(sphere, new PairGeometry(0, 11, 2));
        world.Add(box, new PairGeometry(1, 22, 3));
        world.Add(secondBox, new PairGeometry(1, 33, 5));
        QueryHandle bodies = world.Query(world.QueryDefinition().All<CollisionBounds>());
        var query = Collision.Candidates(world, bodies);
        using var bins = new CollisionPairBins<CollisionNodePair>(jobs, 5, 3);
        var results = new List<DetectedPair>();

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));
        query.DetectPairs(bins, 1, new SumGeometrySizes(results));
        query.DetectPairs(bins, 2, new SumGeometrySizes(results));

        Assert.Equal([
            new DetectedPair(sphere, box, 11, 22, 5),
            new DetectedPair(sphere, secondBox, 11, 33, 7),
            new DetectedPair(box, secondBox, 22, 33, 8)
        ], results);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void BetweenTypeBinsPreserveOverlappingMembershipAndComponentRules()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new PairGeometry(0, 11, 2));
        Entity shared = world.CreateEntity(new PairGeometry(1, 22, 3));
        Entity last = world.CreateEntity(new PairGeometry(1, 33, 5));
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        world.Add(last, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<PairSecondGroup>());
        var query = world.Between(firstGroup, secondGroup).Where(default(DifferentGeometryKind)).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(jobs, 1, 3);
        var results = new List<DetectedPair>();

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));
        query.DetectPairs(bins, 1, new SumGeometrySizes(results));

        Assert.Equal([
            new DetectedPair(first, shared, 11, 22, 5),
            new DetectedPair(first, last, 11, 33, 7)
        ], results);
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    [Fact]
    public void ProductTypeBinsPreserveParticipantOrderAndSharedEntityPairs()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new PairGeometry(0, 11, 2));
        Entity shared = world.CreateEntity(new PairGeometry(1, 22, 3));
        world.Add(first, new PairFirstGroup());
        world.Add(shared, new PairFirstGroup());
        world.Add(shared, new PairSecondGroup());
        QueryHandle firstGroup = world.Query(world.QueryDefinition().All<PairFirstGroup>());
        QueryHandle secondGroup = world.Query(world.QueryDefinition().All<PairSecondGroup>());
        var query = world.Product(secondGroup, firstGroup).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(jobs, 1, 3);

        query.FillPairBinsParallel(bins, default(GeometryPairTypes));

        Assert.Equal([new CollisionGeometryPair(shared, first, 22, 11)], bins.ReadBin(1).ToArray());
        Assert.Equal([new CollisionGeometryPair(shared, shared, 22, 22)], bins.ReadBin(2).ToArray());
        world.ReleaseQuery(secondGroup);
        world.ReleaseQuery(firstGroup);
    }

    [Fact]
    public void UserCandidateRangesRunInsideScheduledWorkAndDeliverEveryPair()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        Entity first = world.CreateEntity(new PairGeometry(0, 11, 2));
        Entity second = world.CreateEntity(new PairGeometry(0, 22, 3));
        Entity third = world.CreateEntity(new PairGeometry(0, 33, 5));
        QueryDefinition definition = world.QueryDefinition().Read<PairGeometry>().Build();
        QueryHandle members = world.Query(definition);
        var query = new RelationQuery<RowPairCandidates, (Entity, Entity)>(new(world, members));
        using var bins = new CollisionPairBins<PairRowRange>(jobs, 2, 3);
        var work = new FillUserPairBins(query, bins);
        var accesses = new JobResourceAccess[QueryJobAccess.Count(definition) + 1];
        int accessCount = QueryJobAccess.CopyAccesses(world, definition, accesses);
        accesses[accessCount] = bins.FillAccess;

        jobs.Schedule(in work, accesses).Complete();

        Assert.Equal([
            new CollisionGeometryPair(first, second, 11, 22),
            new CollisionGeometryPair(first, third, 11, 33),
            new CollisionGeometryPair(second, third, 22, 33)
        ], bins.ReadBin(0).ToArray());
        world.ReleaseQuery(members);
    }
}

public readonly record struct PairGeometry(int Kind, int Identity, float Size) : IComponent;
public readonly record struct DetectedPair(Entity First, Entity Second, int FirstGeometry, int SecondGeometry, float Size);
public readonly struct PairFirstGroup : IComponent;
public readonly struct PairSecondGroup : IComponent;

public readonly struct DifferentGeometryKind : IRelation<PairGeometry, PairGeometry>
{
    public bool Matches(in PairGeometry first, in PairGeometry second) => first.Kind != second.Kind;
}

public readonly record struct PairRowRange(int Start, int Count);

public struct RowPairCandidates(World world, QueryHandle members) : ICollisionPartitionEnumerable<PairRowRange>
{
    private int _rowCount;
    public World World => world;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => members;
    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;
    public void Prepare(scoped RelationQueryViews queries)
    {
        _rowCount = 0;
        foreach (var row in queries.First.Rows)
            _rowCount++;
    }
    public int CreatePartitions(Span<PairRowRange> partitions)
    {
        int firstCount = _rowCount / 2;
        partitions[0] = new(0, firstCount);
        partitions[1] = new(firstCount, _rowCount - firstCount);
        return 2;
    }
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        Prepare(queries);
        return Enumerate(new PairRowRange(0, _rowCount), queries, ref output);
    }
    public bool Enumerate<TOutput>(PairRowRange partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var firstRows = queries.First.Rows;
        int rowIndex = 0;
        while (firstRows.MoveNext(out var first))
        {
            int index = rowIndex++;
            if (index < partition.Start || index >= partition.Start + partition.Count)
                continue;
            var secondRows = firstRows;
            while (secondRows.MoveNext(out var second))
            {
                var pair = EntityOrder.OrderPair(first.Entity, second.Entity);
                if (!output.Add(in pair))
                    return false;
            }
        }
        return true;
    }
}

public readonly struct FillUserPairBins(RelationQuery<RowPairCandidates, (Entity, Entity)> query,
    CollisionPairBins<PairRowRange> bins) : IJob
{
    public void Execute() => query.FillPairBinsParallel(bins, default(GeometryPairTypes));
}

public readonly struct GeometryPairTypes : ICollisionPairClassifier
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<PairGeometry>();

    public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second)
    {
        ref readonly PairGeometry a = ref first.ReadRef<PairGeometry>();
        ref readonly PairGeometry b = ref second.ReadRef<PairGeometry>();
        return new(a.Kind + b.Kind, a.Identity, b.Identity);
    }
}

public readonly struct SumGeometrySizes(List<DetectedPair> results) : ICollisionPairDetection
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<PairGeometry>();

    public void Execute(in CollisionGeometryPair pair, scoped QueryRow first, scoped QueryRow second) =>
        results.Add(new(pair.First, pair.Second, pair.FirstGeometry, pair.SecondGeometry,
            first.ReadRef<PairGeometry>().Size + second.ReadRef<PairGeometry>().Size));
}
