using SomeEngine.ECS.Spatial;
using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class SpatialParallelTests
{
    [Fact]
    public void PartitionedCountAndFillReturnTheFourSerialContactPairs()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var characterA = world.Create();
        var characterB = world.Create();
        var a1 = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        var a2 = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 3) });
        var s = world.Create(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f), CollisionCategories = new(2, 1) });
        world.Create(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f), CollisionCategories = new(2, 2) });
        var d = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f), CollisionCategories = new(1, 3) });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        maintenance.Schedule().Complete();
        using var parallel = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var serial = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        ulong version = world.Version;

        contacts.FillPairsParallel(parallel);
        contacts.FillPairs(serial);

        (Entity, Entity)[] expected = [(a1, b), (a2, b), (b, s), (s, d)];
        Assert.Equal(4, parallel.Count);
        Assert.Equal(expected, Ordered(parallel.Pairs.ToArray()));
        Assert.Equal(4, serial.Count);
        Assert.Equal(expected, Ordered(serial.Pairs.ToArray()));
        Assert.Equal(version, world.Version);

    }

    [Fact]
    public void MaintainedParallelAndHandwrittenFramesConsumeEveryChangedContactPair()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var characterA = world.Create();
        var characterB = world.Create();
        var a = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        var c = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(1, 1) });
        var d = world.Create(new Body { Kind = MotionKind.Static, Bounds = Box(0.6f), CollisionCategories = new(1, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterA });
        world.Add(c, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies,
                SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        using var pairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var handwrittenPairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var serialPairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
        using var handwrittenSerialPairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 5);
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
        Assert.Equal([2, 2, 3, 3], new[] { totals.ContactCounts[a.Id], totals.ContactCounts[b.Id], totals.ContactCounts[c.Id], totals.ContactCounts[d.Id] });
        Assert.Equal(totals.ContactCounts, handwrittenTotals.ContactCounts);
        Assert.Equal(totals.PenetrationDepths, handwrittenTotals.PenetrationDepths);

        world.Set(b, new Hair { Character = characterB });
        world.Set(c, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(2, 2) });
        world.Remove<Body>(d);
        var e = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 1) });
        ulong version = world.Version;
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
        Assert.Equal([2, 2, 0, 0, 2], new[] { totals.ContactCounts[a.Id], totals.ContactCounts[b.Id], totals.ContactCounts[c.Id], totals.ContactCounts[d.Id], totals.ContactCounts[e.Id] });
        Assert.Equal(totals.ContactCounts, handwrittenTotals.ContactCounts);
        Assert.Equal(totals.PenetrationDepths, handwrittenTotals.PenetrationDepths);
        Assert.Equal(version, world.Version);

    }

    [Fact]
    public void CountAndFillKeepTheirComponentInputUntilEveryPartitionCompletes()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        using var gate = new SpatialReadGate();
        var a = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
        var c = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f) });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var writes = world.Query(new QueryDescription().WithStored<Body>());
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies,
                SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), default)
            .Where(new PausingContacts(gate));
        using var maintenance = contacts.CreateMaintenance(jobs);
        maintenance.Schedule().Complete();
        using var pairs = new SpatialPairBuffer<BoundsNodePair>(jobs, partitionCapacity: 3);
        ulong baseline = world.Version;
        JobHandle filled = SpatialCollisionFrames.SchedulePairsParallel(maintenance, pairs);
        JobHandle writer = default;
        try
        {
            Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(10)));
            var move = new MoveBodyJob(writes, b, Box(5), gate);
            writer = jobs.Schedule(in move, ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Body>()));
            gate.Release.Set();
            filled.Complete();
            writer.Complete();
            Assert.Equal(3, pairs.Count);
            Assert.Equal([(a, b), (a, c), (b, c)], Ordered(pairs.Pairs.ToArray()));
            SpatialInputEvent[] events = gate.Events.ToArray();
            Assert.Contains(SpatialInputEvent.ComponentRead, events);
            Assert.Equal(SpatialInputEvent.WriterEntered, events[^1]);

            ulong version = world.Version;
            JobHandle next = SpatialCollisionFrames.SchedulePairsParallel(maintenance, pairs, maintenance.Schedule(writer));
            next.Complete();
            Assert.Equal(1, pairs.Count);
            Assert.Equal([(a, c)], pairs.Pairs.ToArray());
            Assert.Equal(version, world.Version);
            var changedBodies = world.Query(new QueryDescription().WithStored<Body>().WithChanged<Body>(0));
            SpatialQueries.Candidates<Body, BodyBoundsReader>(world, changedBodies, maintenance.Tree, default)
                .Where(default(DefaultContacts))
                .FillPairsParallel(baseline, pairs);
            Assert.Equal(0, pairs.Count);
            Assert.Empty(pairs.Pairs.ToArray());

        }
        finally
        {
            gate.Release.Set();
            filled.Complete();
            writer.Complete();
        }


    }

    private readonly struct MoveBodyJob(Query writes, Entity entity, SpatialBounds bounds,
        SpatialReadGate gate) : IJob
    {
        public void Execute()
        {
            gate.Events.Enqueue(SpatialInputEvent.WriterEntered);
            using QueryScope scope = writes.Open();
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Entities[row] == entity) chunk.Write<Body>(row).Bounds = bounds;
        }
    }

    private static SpatialBounds Box(float x) =>
        new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

    private static (Entity, Entity)[] Ordered(IEnumerable<(Entity, Entity)> pairs) =>
        pairs.OrderBy(pair => pair.Item1.Id).ThenBy(pair => pair.Item1.Generation)
            .ThenBy(pair => pair.Item2.Id).ThenBy(pair => pair.Item2.Generation).ToArray();
}

public readonly struct PausingContacts(SpatialReadGate gate) : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second)
    {
        gate.PauseOnce();
        bool overlaps = first.Bounds.Overlaps(in second.Bounds);
        gate.Events.Enqueue(SpatialInputEvent.ComponentRead);
        return overlaps;
    }
}

public enum SpatialInputEvent
{
    ComponentRead,
    WriterEntered,
}

public sealed class SpatialReadGate : IDisposable
{
    private int _entered;
    public ManualResetEventSlim Started { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    public ConcurrentQueue<SpatialInputEvent> Events { get; } = new();

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
