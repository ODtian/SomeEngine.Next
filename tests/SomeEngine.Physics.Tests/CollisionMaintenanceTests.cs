using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CollisionMaintenanceTests
{
    [Fact]
    public void PositionCategoryAndCharacterWritesRefreshThePersistentContactQuery()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        var c = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(3), CollisionCategories = new(2, 1) });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(0.4f), CollisionCategories = new(2, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterA });
        world.Add(c, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            Collision.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, d), (b, d)], Ordered(pairs));
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, d), (b, d)], Ordered(pairs));

        world.AcquireSystemVersion();
        world.Replace(b, new Hair { Character = characterB });
        world.Replace(c, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(2, 1) });
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, b), (a, c), (a, d), (b, d)], Ordered(pairs));
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, b), (a, c), (a, d), (b, d)], Ordered(pairs));

        world.AcquireSystemVersion();
        world.Replace(b, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(4, 4) });
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        uint version = world.CurrentTick;
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, c), (a, d)], Ordered(pairs));
        Assert.Equal(version, world.CurrentTick);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, c), (a, d)], Ordered(pairs));

        var rebuiltPairs = new List<(Entity, Entity)>();
        var rebuiltOutput = new PairList(rebuiltPairs);
        Collision.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Enumerate(ref rebuiltOutput);
        Assert.Equal([(a, c), (a, d)], Ordered(rebuiltPairs));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void MemberInsertionRemovalAndDestructionRefreshBothMaintainedContactQueries()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var a = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        var c = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(1, 1) });
        world.AddTag<ExcludedBody>(c);
        var bodies = world.Query(world.QueryDefinition().All<Body>().None<ExcludedBody>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            Collision.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        contacts.Enumerate(ref output);
        Assert.Equal([(a, b)], pairs);

        world.Remove<Body>(b);
        var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 1) });
        var e = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 1) });
        world.DestroyEntity(a);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(d, e)], pairs);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(d, e)], pairs);

        world.RemoveTag<ExcludedBody>(c);
        world.AddTag<ExcludedBody>(d);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);
        pairs.Clear();
        Collision.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ExplicitWriterMaintenanceAndContactDependenciesConsumeTheUpdatedFrame()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(3), CollisionCategories = new(1, 1) });
        var c = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var writes = world.Query(world.QueryDefinition().ReadWrite<Body>());
        var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            Collision.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        using var results = new SpatialContactBuffer(jobs, 3, c.Index + 1);
        using var handwrittenResults = new SpatialContactBuffer(jobs, 3, c.Index + 1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var move = new MoveBodyJob(world, writes, b, Box(0.6f), started, release);
        JobHandle movement = ComponentJobAccess<Body>.ScheduleWrite(world, in move);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            JobHandle updated = maintenance.Schedule(movement);
            JobHandle handwrittenUpdated = handwritten.Schedule(movement);
            JobHandle consumed = SpatialCollisionFrames.Schedule(maintenance, results, updated);
            JobHandle handwrittenConsumed = SpatialCollisionFrames.Schedule(handwritten, handwrittenResults, handwrittenUpdated);
            release.Set();
            consumed.Complete();
            handwrittenConsumed.Complete();

            Assert.Equal([(a, b), (a, c), (b, c)], Ordered(results.Pairs.ToArray()));
            Assert.Equal([(a, b), (a, c), (b, c)], Ordered(handwrittenResults.Pairs.ToArray()));
            Assert.Equal([2, 2, 2], new[] { results.ContactCounts[a.Index], results.ContactCounts[b.Index], results.ContactCounts[c.Index] });
            Assert.Equal([2, 2, 2], new[] { handwrittenResults.ContactCounts[a.Index], handwrittenResults.ContactCounts[b.Index], handwrittenResults.ContactCounts[c.Index] });
            Assert.Equal(1.2f, results.PenetrationDepths[a.Index], 5);
            Assert.Equal(1.0f, results.PenetrationDepths[b.Index], 5);
            Assert.Equal(1.4f, results.PenetrationDepths[c.Index], 5);
            Assert.Equal(results.PenetrationDepths, handwrittenResults.PenetrationDepths);
        }
        finally
        {
            release.Set();
            movement.Complete();
        }
        world.ReleaseQuery(writes);
        world.ReleaseQuery(bodies);
    }

    private readonly struct MoveBodyJob(World world, QueryHandle writes, Entity entity, CollisionBounds bounds,
        ManualResetEventSlim started, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            started.Set();
            release.Wait();
            var execution = new MoveExecution(entity, bounds);
            world.ExecuteQuery(writes, ref execution);
        }
    }

    private readonly struct MoveExecution(Entity entity, CollisionBounds bounds) : IQueryExecution
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

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}
