using System.Collections.Concurrent;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class PhysicsExtractionTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public async Task ExtractionPublishesDeclaredDataWhileAnIndependentBodyWriteContinues()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World source = WorldJobs.CreateWorld(scheduler: jobs);
        using var destination = new RenderWorld();
        using var extraction = new RenderExtractionSystems(destination, new WorldOriginRenderExtractor());
        Entity origin = source.CreateEntity(new WorldOrigin(3, 5, 7));
        Entity body = source.CreateEntity(new BodyPose());
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        var write = new WriteBodyAfterRelease(source, body, started, release, events);
        JobHandle physics = ComponentJobAccess<BodyPose>.ScheduleWrite(source, in write);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Task collect = Task.Run(() => extraction.Extract(source));
        try
        {
            await collect.WaitAsync(TimeSpan.FromSeconds(10));
            graphics.Apply(extraction, destination);
            events.Enqueue("extracted");
            Entity mirror = extraction.RequireMirror(origin);
            Assert.Equal(new WorldOrigin(3, 5, 7), destination.Read<RenderWorldOrigin>(mirror).Value);
        }
        finally
        {
            release.Set();
            await physics.WaitAsync();
            await collect;
        }

        Assert.Equal(["physics-started", "extracted", "physics-finished"], events.ToArray());
    }

    [Fact]
    public async Task ExtractionWaitsForTheReferencedDataReadBySupplementalCollection()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World source = WorldJobs.CreateWorld(scheduler: jobs);
        using var destination = new RenderWorld();
        using var extraction = new RenderExtractionSystems(destination, new ReferencedOriginExtractor());
        using var declared = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        WorldOrigin[] values = [new(1, 2, 3)];
        Entity entity = source.CreateEntity(new ReferencedOrigin(values, declared));
        var events = new ConcurrentQueue<string>();
        var write = new WriteOriginAfterRelease(values, started, release, events);
        JobHandle writer = jobs.Schedule(in write, JobResourceAccess.Write(jobs, values));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Task collect = Task.Run(() => extraction.Extract(source));
        try
        {
            Assert.True(declared.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            await collect.WaitAsync(TimeSpan.FromSeconds(10));
            events.Enqueue("extracted");
            graphics.Apply(extraction, destination);
            Entity mirror = extraction.RequireMirror(entity);
            Assert.Equal(new WorldOrigin(11, 13, 17), destination.Read<RenderWorldOrigin>(mirror).Value);
        }
        finally
        {
            release.Set();
            await writer.WaitAsync();
            await collect;
        }
        Assert.Equal(["writer-started", "writer-finished", "extracted"], events.ToArray());
    }

    private readonly record struct ReferencedOrigin(WorldOrigin[] Values, ManualResetEventSlim Declared) : IComponent;

    [Fact]
    public void ReferencedExtractionHelpsIndependentBodyJobsUsingTheirOwnAccesses()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World source = WorldJobs.CreateWorld(scheduler: jobs);
        using var destination = new RenderWorld();
        using var extraction = new RenderExtractionSystems(destination, new ReferencedOriginExtractor());
        using var declared = new ManualResetEventSlim();
        WorldOrigin[] values = [new(1, 2, 3)];
        Entity entity = source.CreateEntity(new ReferencedOrigin(values, declared));
        Entity body = source.CreateEntity(new BodyPose());
        var events = new ConcurrentQueue<string>();
        var bodyWrite = new SetBodyPosition(source, body, events);
        JobHandle bodyWritten = ComponentJobAccess<BodyPose>.ScheduleWrite(source, in bodyWrite);
        async Task WriteOrigin()
        {
            await bodyWritten.WaitAsync();
            values[0] = new(11, 13, 17);
            events.Enqueue("origin-written");
        }
        JobHandle originWritten = jobs.CreateExternalFenceHandle(WriteOrigin(),
            [JobResourceAccess.Write(jobs, values)]);

        extraction.Extract(source);
        events.Enqueue("extracted");
        graphics.Apply(extraction, destination);

        Entity mirror = extraction.RequireMirror(entity);
        Assert.Equal(new WorldOrigin(11, 13, 17), destination.Read<RenderWorldOrigin>(mirror).Value);
        Assert.Equal(new System.Numerics.Vector3(19, 0, 0), source.Read<BodyPose>(body).Position);
        Assert.Equal(["body-written", "origin-written", "extracted"], events.ToArray());
        originWritten.Complete();
    }

    private readonly struct SetBodyPosition(World world, Entity body, ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            world.Replace(body, new BodyPose { Position = new(19, 0, 0) });
            events.Enqueue("body-written");
        }
    }

    [Fact]
    public void DirectSourceCollectionKeepsWorldMutationRestricted()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World source = WorldJobs.CreateWorld(scheduler: jobs);
        using var destination = new RenderWorld();
        Entity body = source.CreateEntity(new BodyPose());
        source.CreateEntity(new WorldOrigin(3, 5, 7));
        using var extraction = new RenderExtractionSystems(destination, new SourceMutationExtractor(body));

        Assert.Throws<InvalidOperationException>(() => extraction.Extract(source));

        Assert.Equal(System.Numerics.Vector3.Zero, source.Read<BodyPose>(body).Position);
    }

    private sealed class SourceMutationExtractor(Entity body) : IRenderExtractionSystem
    {
        public void DeclareReads(RenderExtractionQuery query) => query.ReadOptional<WorldOrigin>();
        public void Reset() { }
        public void Collect(QueryChunkView chunk) { }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => false;
        public bool CompleteSourceCollection(World source, bool fullCollection)
        {
            source.Replace(body, new BodyPose { Position = new(19, 0, 0) });
            return false;
        }
        public void Apply(RenderExtractionContext context) { }
        public void ApplyChanges(RenderExtractionContext context) { }
    }

    private readonly struct ReferencedOriginResources : IJobComponentResources<ReferencedOrigin>
    {
        public static void Collect(in ReferencedOrigin component, ref JobResourceAccessBuilder resources)
        {
            component.Declared.Set();
            resources.Read(component.Values.AsMemory());
        }
    }

    private sealed class ReferencedOriginExtractor : IRenderExtractionSystem
    {
        private readonly List<Entity> _entities = [];
        private readonly List<(Entity Entity, WorldOrigin Value)> _values = [];
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<ReferencedOrigin, ReferencedOriginResources>();
            query.Write<RenderWorldOrigin>();
        }
        public void Reset() { _entities.Clear(); _values.Clear(); }
        public void Collect(QueryChunkView chunk)
        {
            if (chunk.Has<ReferencedOrigin>()) _entities.AddRange(chunk.Entities);
        }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
        { Collect(chunk); return true; }
        public bool CompleteSourceCollection(World source, bool fullCollection)
        {
            foreach (Entity entity in _entities)
                _values.Add((entity, source.Read<ReferencedOrigin>(entity).Values[0]));
            return true;
        }
        public void Apply(RenderExtractionContext context)
        {
            foreach (var value in _values)
                context.Upsert(context.RetainMirror(value.Entity), new RenderWorldOrigin(value.Value));
        }
        public void ApplyChanges(RenderExtractionContext context) => Apply(context);
    }

    private readonly struct WriteOriginAfterRelease(WorldOrigin[] values, ManualResetEventSlim started,
        ManualResetEventSlim release, ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            events.Enqueue("writer-started");
            started.Set();
            release.Wait();
            values[0] = new(11, 13, 17);
            events.Enqueue("writer-finished");
        }
    }

    private readonly struct WriteBodyAfterRelease(World world, Entity body, ManualResetEventSlim started,
        ManualResetEventSlim release, ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            events.Enqueue("physics-started");
            started.Set();
            release.Wait();
            world.Replace(body, new BodyPose { Position = new(11, 0, 0) });
            events.Enqueue("physics-finished");
        }
    }
}
