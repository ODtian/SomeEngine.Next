using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class SpatialParallelTests
{
    [Fact]
    public void PartitionedCountAndFillReturnTheFourSerialContactPairs()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 3) });
        var s = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f), CollisionCategories = new(2, 1) });
        world.CreateEntity(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f), CollisionCategories = new(2, 2) });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f), CollisionCategories = new(1, 3) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        maintenance.Schedule().Complete();
        using var parallel = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        using var serial = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        uint version = world.CurrentTick;

        contacts.FillPairsParallel(parallel);
        contacts.FillPairs(serial);

        (Entity, Entity)[] expected = [(a1, b), (a2, b), (b, s), (s, d)];
        Assert.Equal(4, parallel.Count);
        Assert.Equal(expected, Ordered(parallel.Pairs.ToArray()));
        Assert.Equal(4, serial.Count);
        Assert.Equal(expected, Ordered(serial.Pairs.ToArray()));
        Assert.Equal(version, world.CurrentTick);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void MaintainedParallelAndHandwrittenFramesConsumeEveryChangedContactPair()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        var c = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(1, 1) });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(0.6f), CollisionCategories = new(1, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterA });
        world.Add(c, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies,
                Collision.Build<Body, BodyBoundsReader>(world, bodies, default), default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            Collision.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        using var pairs = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        using var handwrittenPairs = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        using var serialPairs = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        using var handwrittenSerialPairs = new CollisionPairBuffer(jobs, partitionCapacity: 5);
        using var totals = new SpatialContactTotals(jobs, entityCapacity: 32);
        using var handwrittenTotals = new SpatialContactTotals(jobs, entityCapacity: 32);
        JobHandle first = SpatialCollisionFrames.ScheduleParallel(maintenance, pairs, totals, maintenance.Schedule());
        JobHandle handwrittenFirst = SpatialCollisionFrames.ScheduleParallel(handwritten, handwrittenPairs, handwrittenTotals, handwritten.Schedule());
        first.Complete();
        handwrittenFirst.Complete();
        contacts.FillPairs(serialPairs);
        handwritten.Query.FillPairs(handwrittenSerialPairs);
        (Entity, Entity)[] initial = [(a, c), (a, d), (b, c), (b, d), (c, d)];
        Assert.Equal(5, pairs.Count);
        Assert.Equal(initial, Ordered(pairs.Pairs.ToArray()));
        Assert.Equal(initial, Ordered(handwrittenPairs.Pairs.ToArray()));
        Assert.Equal(initial, Ordered(serialPairs.Pairs.ToArray()));
        Assert.Equal(initial, Ordered(handwrittenSerialPairs.Pairs.ToArray()));
        Assert.Equal([2, 2, 3, 3], new[] { totals.ContactCounts[a.Index], totals.ContactCounts[b.Index], totals.ContactCounts[c.Index], totals.ContactCounts[d.Index] });
        Assert.Equal(totals.ContactCounts, handwrittenTotals.ContactCounts);
        Assert.Equal(totals.PenetrationDepths, handwrittenTotals.PenetrationDepths);

        world.AcquireSystemVersion();
        world.Replace(b, new Hair { Character = characterB });
        world.Replace(c, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(2, 2) });
        world.Remove<Body>(d);
        var e = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 1) });
        uint version = world.CurrentTick;
        JobHandle next = SpatialCollisionFrames.ScheduleParallel(maintenance, pairs, totals, maintenance.Schedule(first));
        JobHandle handwrittenNext = SpatialCollisionFrames.ScheduleParallel(handwritten, handwrittenPairs, handwrittenTotals, handwritten.Schedule(handwrittenFirst));
        next.Complete();
        handwrittenNext.Complete();
        contacts.FillPairs(serialPairs);
        handwritten.Query.FillPairs(handwrittenSerialPairs);

        (Entity, Entity)[] updated = [(a, b), (a, e), (b, e)];
        Assert.Equal(3, pairs.Count);
        Assert.Equal(updated, Ordered(pairs.Pairs.ToArray()));
        Assert.Equal(updated, Ordered(handwrittenPairs.Pairs.ToArray()));
        Assert.Equal(updated, Ordered(serialPairs.Pairs.ToArray()));
        Assert.Equal(updated, Ordered(handwrittenSerialPairs.Pairs.ToArray()));
        Assert.Equal([2, 2, 0, 0, 2], new[] { totals.ContactCounts[a.Index], totals.ContactCounts[b.Index], totals.ContactCounts[c.Index], totals.ContactCounts[d.Index], totals.ContactCounts[e.Index] });
        Assert.Equal(totals.ContactCounts, handwrittenTotals.ContactCounts);
        Assert.Equal(totals.PenetrationDepths, handwrittenTotals.PenetrationDepths);
        Assert.Equal(version, world.CurrentTick);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void CountAndFillKeepTheirComponentInputUntilEveryPartitionCompletes()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        using var gate = new SpatialReadGate();
        var a = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var c = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f) });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var writes = world.Query(world.QueryDefinition().ReadWrite<Body>());
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies,
                Collision.Build<Body, BodyBoundsReader>(world, bodies, default), default)
            .Where(new PausingContacts(gate));
        using var maintenance = contacts.CreateMaintenance(jobs);
        maintenance.Schedule().Complete();
        using var pairs = new CollisionPairBuffer(jobs, partitionCapacity: 3);
        uint baseline = world.CurrentTick;
        JobHandle filled = SpatialCollisionFrames.SchedulePairsParallel(maintenance, pairs);
        JobHandle writer = default;
        try
        {
            Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(10)));
            var move = new MoveBodyJob(world, writes, b, Box(5));
            writer = ComponentJobAccess<Body>.ScheduleWrite(world, in move);
            gate.Release.Set();
            filled.Complete();
            writer.Complete();
            Assert.Equal(3, pairs.Count);
            Assert.Equal([(a, b), (a, c), (b, c)], Ordered(pairs.Pairs.ToArray()));

            uint version = world.CurrentTick;
            JobHandle next = SpatialCollisionFrames.SchedulePairsParallel(maintenance, pairs, maintenance.Schedule(writer));
            next.Complete();
            Assert.Equal(1, pairs.Count);
            Assert.Equal([(a, c)], pairs.Pairs.ToArray());
            Assert.Equal(version, world.CurrentTick);
            var changedBodies = world.Query(world.QueryDefinition().All<Body>().Changed<Body>());
            Collision.Candidates<Body, BodyBoundsReader>(world, changedBodies, maintenance.Tree, default)
                .Where(default(DefaultContacts))
                .FillPairsParallel(baseline, pairs);
            Assert.Equal(0, pairs.Count);
            Assert.Empty(pairs.Pairs.ToArray());
            world.ReleaseQuery(changedBodies);
        }
        finally
        {
            gate.Release.Set();
            filled.Complete();
            writer.Complete();
        }
        world.ReleaseQuery(writes);
        world.ReleaseQuery(bodies);
    }

    private readonly struct MoveBodyJob(World world, QueryHandle writes, Entity entity, CollisionBounds bounds) : IJob
    {
        public void Execute()
        {
            var execution = new MoveBounds(entity, bounds);
            world.ExecuteQuery(writes, ref execution);
        }
    }

    private readonly struct MoveBounds(Entity entity, CollisionBounds bounds) : IQueryExecution
    {
        public void Execute(QueryCursor query)
        {
            foreach (var row in query.Rows)
            {
                if (row.Entity == entity)
                    row.ReadWrite<Body>().Bounds = bounds;
            }
        }
    }

    private static CollisionBounds Box(float x) =>
        new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

    private static (Entity, Entity)[] Ordered(IEnumerable<(Entity, Entity)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Index).ThenBy(pair => pair.Item1.Generation)
            .ThenBy(pair => pair.Item2.Index).ThenBy(pair => pair.Item2.Generation).ToArray();
}

public readonly struct PausingContacts(SpatialReadGate gate) : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second)
    {
        gate.PauseOnce();
        return true;
    }
}

public sealed class SpatialReadGate : IDisposable
{
    private int _entered;
    public ManualResetEventSlim Started { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public void PauseOnce()
    {
        if (Interlocked.Exchange(ref _entered, 1) == 0)
        {
            Started.Set();
            Release.Wait();
        }
    }

    public void Dispose()
    {
        Started.Dispose();
        Release.Dispose();
    }
}
