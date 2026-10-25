using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class QueryReadResourceTests : JobTest
{
    [Fact]
    public void ScheduledEntityReadUsesTheReplacementAfterItsInputCompletes()
    {
        World world = CreateJobWorld();
        Entity first = world.CreateEntity(new ReferencedHeight(new HeightSamples([1, 2]), 0));
        Entity second = world.CreateEntity(new ReferencedHeight(new HeightSamples([3, 4]), 1));
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<ReferencedHeight>());
        var resources = new QueryReadResources();
        resources.Add<ReferencedHeight, ReadHeightResources>();
        var values = new List<int>();
        var ready = new TaskCompletionSource();
        JobHandle input = Jobs.CreateExternalFenceHandle(ready.Task);
        JobHandle replaced = new ReplaceComponentHeight(new HeightSamples([19, 37])).Schedule(world, input);
        var job = new ReadHeights(world, query, resources, new EntityList([first, second]), values);

        JobHandle read = resources.ScheduleEntities(world, query, new EntityList([first, second]), in job,
            [JobResourceAccess.Write(Jobs, values)], replaced);
        ready.SetResult();
        read.Complete();

        Assert.Equal([19, 37], values);
        world.ReleaseQuery(query);
    }

    [Fact]
    public void ScheduledEntityReadRebindsWhenTheComponentChangesBeforeResourceAccess()
    {
        World world = CreateJobWorld();
        using var collected = new ManualResetEventSlim();
        int[] original = [3];
        Entity entity = world.CreateEntity(new SignaledValues(original, collected));
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<SignaledValues>());
        var resources = new QueryReadResources();
        resources.Add<SignaledValues, SignaledValueResources>();
        var values = new List<int>();
        var ready = new TaskCompletionSource();
        JobHandle producer = Jobs.CreateExternalFenceHandle(ready.Task, [JobResourceAccess.Write(Jobs, original)]);
        var job = new ReadSignaledValues(world, query, resources, new EntityList([entity]), values);
        JobHandle read = resources.ScheduleEntities(world, query, new EntityList([entity]), in job,
            [JobResourceAccess.Write(Jobs, values)]);

        try
        {
            Assert.True(collected.Wait(TimeSpan.FromSeconds(5)));
            world.Replace(entity, new SignaledValues([43], null));
        }
        finally { ready.SetResult(); }
        read.Complete();

        Assert.Equal([43], values);
        producer.Complete();
        world.ReleaseQuery(query);
    }

    private readonly struct EntityList(Entity[] entities) : IQueryReadEntities
    {
        public int Count => entities.Length;
        public Entity GetEntity(int index) => entities[index];
    }

    private readonly struct ReadHeights(World world, QueryHandle query, QueryReadResources resources,
        EntityList entities, List<int> values) : IJob
    {
        public void Execute()
        {
            var work = this;
            world.ExecuteReadQuery(query, ref work,
                static (scoped QueryCursor view, ref ReadHeights state) => state.Read(view));
        }

        private void Read(scoped QueryCursor view)
        {
            var work = this;
            resources.ExecuteEntities(view, entities, ref work,
                static (scoped QueryCursor current, ref ReadHeights state) => state.Sample(current));
        }

        private void Sample(scoped QueryCursor view)
        {
            for (int index = 0; index < entities.Count; index++)
            {
                Assert.True(view.TryReadRef<ReferencedHeight>(entities.GetEntity(index), out var source));
                values.Add(source.Value.Samples.Values[source.Value.Index]);
            }
        }
    }

    private readonly record struct SignaledValues(int[] Values, ManualResetEventSlim? Collected) : IComponent;

    private readonly struct SignaledValueResources : IJobComponentResources<SignaledValues>
    {
        public static void Collect(in SignaledValues component, ref JobResourceAccessBuilder resources)
        {
            resources.Read(component.Values);
            component.Collected?.Set();
        }
    }

    private readonly struct ReadSignaledValues(World world, QueryHandle query, QueryReadResources resources,
        EntityList entities, List<int> values) : IJob
    {
        public void Execute()
        {
            var work = this;
            world.ExecuteReadQuery(query, ref work,
                static (scoped QueryCursor view, ref ReadSignaledValues state) => state.Read(view));
        }

        private void Read(scoped QueryCursor view)
        {
            var work = this;
            resources.ExecuteEntities(view, entities, ref work,
                static (scoped QueryCursor current, ref ReadSignaledValues state) => state.Sample(current));
        }

        private void Sample(scoped QueryCursor view)
        {
            Assert.True(view.TryReadRef<SignaledValues>(entities.GetEntity(0), out var source));
            values.Add(source.Value.Values[0]);
        }
    }
}
