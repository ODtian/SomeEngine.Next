using SomeEngine.ECS.Spatial;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CollisionMaintenanceTests
{
    [Fact]
    public void DefaultBoundsWritesRefreshTheMaintainedTree()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        world.Create(Box(0));
        Entity second = world.Create(Box(3));
        Query members = world.Query<SpatialBounds>();
        using var maintenance = SpatialQueries.Candidates(world, members).CreateMaintenance(jobs);

        maintenance.Schedule().Complete();
        Assert.Equal(0, maintenance.Query.Count());

        world.Set(second, Box(.25f));
        maintenance.Schedule().Complete();
        Assert.Equal(1, maintenance.Query.Count());
    }

    [Fact]
    public void PositionCategoryAndCharacterWritesRefreshThePersistentContactQuery()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var characterA = world.Create();
        var characterB = world.Create();
        var a = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 3) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 3) });
        var c = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(3), CollisionCategories = new(2, 1) });
        var d = world.Create(new Body { Kind = MotionKind.Static, Bounds = Box(0.4f), CollisionCategories = new(2, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterA });
        world.Add(c, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, d), (b, d)], Ordered(pairs));
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, d), (b, d)], Ordered(pairs));

        world.Set(b, new Hair { Character = characterB });
        world.Set(c, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(2, 1) });
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, b), (a, c), (a, d), (b, d)], Ordered(pairs));
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, b), (a, c), (a, d), (b, d)], Ordered(pairs));

        world.Set(b, new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(4, 4) });
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        ulong version = world.Version;
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(a, c), (a, d)], Ordered(pairs));
        Assert.Equal(version, world.Version);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(a, c), (a, d)], Ordered(pairs));

        var rebuiltPairs = new List<(Entity, Entity)>();
        var rebuiltOutput = new PairList(rebuiltPairs);
        SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Enumerate(ref rebuiltOutput);
        Assert.Equal([(a, c), (a, d)], Ordered(rebuiltPairs));

    }

    [Fact]
    public void MemberInsertionRemovalAndDestructionRefreshBothMaintainedContactQueries()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var a = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        var c = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.4f), CollisionCategories = new(1, 1) });
        world.Add(c, new ExcludedBody());
        var bodies = world.Query(new QueryDescription().WithStored<Body>().Without<ExcludedBody>());
        var tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        contacts.Enumerate(ref output);
        Assert.Equal([(a, b)], pairs);

        world.Remove<Body>(b);
        var d = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f), CollisionCategories = new(1, 1) });
        var e = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.8f), CollisionCategories = new(1, 1) });
        world.Destroy(a);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(d, e)], pairs);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(d, e)], pairs);

        world.Remove<ExcludedBody>(c);
        world.Add(d, new ExcludedBody());
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        pairs.Clear();
        contacts.Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);
        pairs.Clear();
        handwritten.Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);
        pairs.Clear();
        SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Enumerate(ref output);
        Assert.Equal([(c, e)], pairs);

    }

    [Fact]
    public void ExplicitWriterMaintenanceAndContactDependenciesConsumeTheUpdatedFrame()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var characterA = world.Create();
        var characterB = world.Create();
        var a = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0), CollisionCategories = new(1, 1) });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(3), CollisionCategories = new(1, 1) });
        var c = world.Create(new Body { Kind = MotionKind.Static, Bounds = Box(0.2f), CollisionCategories = new(1, 1) });
        world.Add(a, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var writes = world.Query(new QueryDescription().WithStored<Body>());
        var tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default);
        var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
            .Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        using var maintenance = contacts.CreateMaintenance(jobs);
        using var handwritten = new HandwrittenSpatialMaintenance(world, bodies,
            SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default), jobs);
        maintenance.Schedule().Complete();
        handwritten.Schedule().Complete();
        using var results = new SpatialContactBuffer(jobs, 3, c.Id + 1);
        using var handwrittenResults = new SpatialContactBuffer(jobs, 3, c.Id + 1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var move = new MoveBodyJob(writes, b, Box(0.6f), started, release);
        JobHandle movement = jobs.Schedule(in move, ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Body>()));
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
            Assert.Equal([2, 2, 2], new[] { results.ContactCounts[a.Id], results.ContactCounts[b.Id], results.ContactCounts[c.Id] });
            Assert.Equal([2, 2, 2], new[] { handwrittenResults.ContactCounts[a.Id], handwrittenResults.ContactCounts[b.Id], handwrittenResults.ContactCounts[c.Id] });
            Assert.Equal(1.2f, results.PenetrationDepths[a.Id], 5);
            Assert.Equal(1.0f, results.PenetrationDepths[b.Id], 5);
            Assert.Equal(1.4f, results.PenetrationDepths[c.Id], 5);
            Assert.Equal(results.PenetrationDepths, handwrittenResults.PenetrationDepths);
        }
        finally
        {
            release.Set();
            movement.Complete();
        }


    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorldEnableChangesRefreshMaintainedMembers(bool enabled)
    {
        using var data = new CollisionMemberData(enabled);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        data.Maintenance.Schedule().Complete();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);

        if (enabled) data.World.Enable<CollisionActivity>(data.Second);
        else data.World.Disable<CollisionActivity>(data.Second);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Equal([(data.First, data.Second)], pairs);

        if (enabled) data.World.Disable<CollisionActivity>(data.Second);
        else data.World.Enable<CollisionActivity>(data.Second);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopedEnableChangesRefreshMaintainedMembers(bool enabled)
    {
        using var data = new CollisionMemberData(enabled);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        data.Maintenance.Schedule().Complete();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);

        using (data.World.OpenStructureWrite())
            data.World.SetEnabled<CollisionActivity>(data.Second, enabled);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Equal([(data.First, data.Second)], pairs);

        using (data.World.OpenStructureWrite())
            data.World.SetEnabled<CollisionActivity>(data.Second, !enabled);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChunkSelectedEnableChangesRefreshMaintainedMembers(bool enabled)
    {
        using var data = new CollisionMemberData(enabled);
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        data.Maintenance.Schedule().Complete();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);

        Entity firstSelected = default;
        using (QueryScope scope = data.Writes.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities)
                    if (entity == data.Second) firstSelected = entity;
        using (data.World.OpenStructureWrite())
            data.World.SetEnabled<CollisionActivity>(firstSelected, enabled);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Equal([(data.First, data.Second)], pairs);

        Entity secondSelected = default;
        using (QueryScope scope = data.Writes.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities)
                    if (entity == data.Second) secondSelected = entity;
        using (data.World.OpenStructureWrite())
            data.World.SetEnabled<CollisionActivity>(secondSelected, !enabled);
        data.Maintenance.Schedule().Complete();
        pairs.Clear();
        data.Contacts.Enumerate(ref output);
        Assert.Empty(pairs);
    }

    private sealed class CollisionMemberData : IDisposable
    {
        private readonly Scheduler _jobs;
        private readonly Query _bodies;

        public CollisionMemberData(bool enabled)
        {
            _jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
            World = new World(execution: new WorldJobs(_jobs));
            First = World.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
            Second = World.Create(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
            World.Add(First, new CollisionActivity());
            World.Add(Second, new CollisionActivity());
            bool membersEnabled = enabled;
            World.Disable<CollisionActivity>(membersEnabled ? Second : First);
            var definition = new QueryDescription().WithStored<Body>();
            _bodies = World.Query(membersEnabled
                ? definition.With<CollisionActivity>()
                : definition.WithDisabled<CollisionActivity>());
            Writes = World.Query(new QueryDescription().WithStored<CollisionActivity>());
            Contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(World, _bodies,
                SpatialQueries.Build<Body, BodyBoundsReader>(World, _bodies, default), default);
            Maintenance = Contacts.CreateMaintenance(_jobs);
        }

        public World World { get; }
        public Entity First { get; }
        public Entity Second { get; }
        public Query Writes { get; }
        public RelationQuery<TreeCandidates<Body, BodyBoundsReader>, (Entity, Entity)> Contacts { get; }
        public SpatialMaintenance<Body, BodyBoundsReader, SpatialAll, GroupMatch> Maintenance { get; }

        public void Dispose()
        {
            Maintenance.Dispose();


            World.Dispose();
            _jobs.Dispose();
        }
    }

    private readonly struct MoveBodyJob(Query writes, Entity entity, SpatialBounds bounds,
        ManualResetEventSlim started, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            started.Set();
            release.Wait();
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

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}

public struct CollisionActivity : IEnableableComponent;
