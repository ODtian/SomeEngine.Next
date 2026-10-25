using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class EntityQueryJobsTests
{
    [Fact]
    public void DerivedReadQueryKeepsTheOriginalMembersAndBufferValues()
    {
        using var world = new World();
        Entity selected = world.CreateEntity(new PointValue(7));
        world.AddTag<Chosen>(selected);
        world.AddBuffer<PathValue>(selected);
        world.ExecuteBufferWrite<PathValue>(selected, static values => values.Add(new(5)));
        Entity other = world.CreateEntity(new PointValue(19));
        world.AddBuffer<PathValue>(other);
        world.ExecuteBufferWrite<PathValue>(other, static values => values.Add(new(23)));
        QueryDefinition original = new QueryDefinitionBuilder().All<Chosen>().ReadWrite<PointValue>()
            .WriteBuffer<PathValue>().Build();
        QueryHandle reads = world.Query(original.AsReadOnly());
        var values = new List<(int Point, int Path)>();

        world.ExecuteReadQuery(reads, query =>
        {
            foreach (QueryRow row in query.Rows)
                values.Add((row.Read<PointValue>().Value, row.ReadBuffer<PathValue>()[0].Value));
        });

        Assert.Equal([(7, 5)], values);
        world.ReleaseQuery(reads);
    }

    [Fact]
    public void SeparateEntityWritesAdvanceTogetherWhileReadingTheSameFixedEntity()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new PointValue(1));
        world.Add(first, new SpeedValue(2));
        Entity second = world.CreateEntity(new PointValue(3));
        world.Add(second, new SpeedValue(4));
        Entity anchor = world.CreateEntity(new PointValue(10));
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>().ReadWrite<SpeedValue>());
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<PointValue>());
        using var firstEntered = new ManualResetEventSlim();
        using var secondWritten = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var work = new PointWork(writes, reads, [[first], [second]], [anchor]);

        JobHandle complete = EntityQueryJobs.Schedule(world, in work, new MovePoints(firstEntered, secondWritten, release));
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(secondWritten.Wait(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); }
        complete.Complete();

        Assert.Equal(11, world.Read<PointValue>(first).Value);
        Assert.Equal(13, world.Read<PointValue>(second).Value);
        Assert.Equal(12, world.Read<SpeedValue>(first).Value);
        Assert.Equal(14, world.Read<SpeedValue>(second).Value);
        Assert.Equal(world.WriteVersion<PointValue>(first), world.WriteVersion<PointValue>(second));
        world.ReleaseQuery(reads);
        world.ReleaseQuery(writes);
    }

    [Fact]
    public void CapturedEntityRangesFollowTheEntityAfterARegisteredStructuralMove()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity entity = world.CreateEntity(new PointValue(7));
        QueryHandle query = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        using var captured = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var work = new CapturedWork(query, [entity], captured, release);

        JobHandle complete = EntityQueryJobs.Schedule(world, in work, new IncrementPoints());
        Assert.True(captured.Wait(TimeSpan.FromSeconds(10)));
        JobHandle moved = scheduler.Schedule(new AddChosen(world, entity), [RelationshipJobAccess.TopologyWrite(world)]);
        release.Set();
        complete.Complete();
        moved.Complete();

        Assert.True(world.Has<Chosen>(entity));
        Assert.Equal(8, world.Read<PointValue>(entity).Value);
        world.ReleaseQuery(query);
    }

    [Fact]
    public void EntityRangesBindTheReplacementReferenceAfterItsProducerCompletes()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var collected = new ManualResetEventSlim();
        int[] original = [3];
        Entity input = world.CreateEntity(new ReferencedSamples(original, collected, 1));
        Entity output = input;
        world.Add(output, new PointValue(0));
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<ReferencedSamples>());
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        var resources = new QueryReadResources();
        resources.Add<ReferencedSamples, ReadSampleResources>();
        var ready = new TaskCompletionSource();
        JobHandle producer = scheduler.CreateExternalFenceHandle(ready.Task, [JobResourceAccess.Write(scheduler, original)]);
        var work = new ResourceWork(reads, writes, [input], [output]);

        JobHandle complete = EntityQueryJobs.Schedule(world, in work, new ReadSamples(scheduler), resources: resources);
        try
        {
            Assert.True(collected.Wait(TimeSpan.FromSeconds(10)));
            world.Replace(input, new ReferencedSamples([43], null, 2));
        }
        finally { ready.SetResult(); }
        complete.Complete();
        producer.Complete();

        Assert.Equal(43, world.Read<PointValue>(output).Value);
        world.ReleaseQuery(writes);
        world.ReleaseQuery(reads);
    }

    private readonly record struct ReferencedSamples(int[] Values, ManualResetEventSlim? Collected, long Version) : IComponent;
    private readonly struct ReadSampleResources : IJobComponentResources<ReferencedSamples>
    {
        public static long GetVersion(in ReferencedSamples component) => component.Version;
        public static void Collect(in ReferencedSamples component, ref JobResourceAccessBuilder resources)
        {
            resources.Read(component.Values);
            component.Collected?.Set();
        }
    }
    private readonly struct ResourceWork(QueryHandle reads, QueryHandle writes, Entity[] inputs, Entity[] outputs) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => 2;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => queryIndex == 0 ? reads : writes;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => queryIndex == 0 ? inputs : outputs;
    }
    private readonly struct ReadSamples(Scheduler scheduler) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            EntityQuery source = queries.Get(0);
            source.TryReadRef<ReferencedSamples>(source.Entities[0], out var samples);
            scheduler.ExecuteParallel(new ReadSampleRange(samples.Value.Values), 1, 1,
                [JobResourceAccess.Read(scheduler, samples.Value.Values)]);
            foreach (QueryRow row in queries.Get(1).Rows)
                row.ReadWrite<PointValue>() = new(samples.Value.Values[0]);
        }
    }
    private readonly record struct ReadSampleRange(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Assert.Equal(43, Values[index]);
    }

    private readonly struct CapturedWork(QueryHandle query, Entity[] entities, ManualResetEventSlim captured,
        ManualResetEventSlim release) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => query;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex)
        {
            captured.Set();
            release.Wait();
            return entities;
        }
    }
    private readonly record struct AddChosen(World World, Entity Entity) : IJob
    {
        public void Execute() => World.AddTag<Chosen>(Entity);
    }
    private readonly struct IncrementPoints : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            foreach (QueryRow row in queries.Get(0).Rows)
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + 1);
        }
    }

    private readonly record struct PointValue(int Value) : IComponent;
    private readonly record struct SpeedValue(int Value) : IComponent;
    private readonly struct Chosen : ITag;
    private readonly record struct PathValue(int Value) : IBufferElement;

    private readonly struct PointWork(QueryHandle writes, QueryHandle reads, Entity[][] points, Entity[] anchors) : IEntityQueryWork
    {
        public int Count => points.Length;
        public int QueryCount => 2;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => queryIndex == 0 ? writes : reads;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => queryIndex == 0 ? points[workIndex] : anchors;
    }

    private readonly struct MovePoints(ManualResetEventSlim firstEntered, ManualResetEventSlim secondWritten, ManualResetEventSlim release)
        : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            if (index == 0)
            {
                firstEntered.Set();
                release.Wait();
            }
            EntityQuery inputs = queries.Get(1);
            Assert.True(inputs.TryReadRef<PointValue>(inputs.Entities[0], out var anchor));
            foreach (QueryRow row in queries.Get(0).Rows)
            {
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + anchor.Value.Value);
                row.ReadWrite<SpeedValue>() = new(row.Read<SpeedValue>().Value + anchor.Value.Value);
            }
            if (index == 1) secondWritten.Set();
        }
    }
}
