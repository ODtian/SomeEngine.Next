using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class GeneratedMetadataTests
{
    [Fact]
    public void RegisteredSchemaAndStorageDriveOrdinaryEdgeAndQueryJobAccess()
    {
        GeneratedEcsTypes.RegisterRelationSchema<RegisteredLink<int>>(
            new RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair, false));
        GeneratedEcsTypes.RegisterJobStorage<RegisteredValue<int>>(true);
        GeneratedEcsTypes.RegisterJobStorage<DirectedRelationEndpoints<RegisteredLink<int>>>(true);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var source = world.CreateEntity(new RegisteredValue<int> { Value = 4 });
        var target = world.CreateEntity();
        var edge = world.CreateRelation(source, target, new RegisteredLink<int>());
        var values = world.QueryDefinition().Read<RegisteredValue<int>>().Build();
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[QueryJobAccess.Count(values)];

        int count = QueryJobAccess.CopyAccesses(world, values, accesses);
        var observed = new List<Entity>();
        var job = new ReadRegisteredEdge(world, edge, observed);
        RelationJobAccess<RegisteredLink<int>>.ScheduleEndpointsRead(world, job).Complete();

        Assert.Equal(2, count);
        Assert.Equal(new[] { target }, observed);
        Assert.Throws<InvalidOperationException>(() => world.CreateRelation(source, target, new RegisteredLink<int>()));
    }

    private readonly struct ReadRegisteredEdge(World world, RelationEdge<RegisteredLink<int>> edge,
        List<Entity> observed) : IJob
    {
        public void Execute() => observed.Add(world.GetDirectedRelationEndpoints(edge).Target);
    }

    [Fact]
    public void ClosedComponentsUseOrdinaryAndGeneratedJobStorageChecks()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var entity = world.CreateEntity(new RegisteredValue<int> { Value = 4 });

        new IncreaseRegisteredValues().Schedule(world).Complete();

        Assert.Equal(5, world.Read<RegisteredValue<int>>(entity).Value);
        Assert.Throws<InvalidOperationException>(() => ComponentJobAccess<NativeAddress<int>>.Read(world));
        var definition = world.QueryDefinition().Read<NativeAddress<int>>().Build();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, accesses);
        });
    }

    [Fact]
    public void ClosedSchemaPreservesUndirectedEndpointRoles()
    {
        using var world = new World();
        var first = world.CreateEntity();
        var second = world.CreateEntity();

        var edge = world.CreateRelation(second, first, new KnownLink<int>());
        var endpoints = world.GetUndirectedRelationEndpoints(edge);

        Assert.Equal(second, endpoints.EndpointA);
        Assert.Equal(first, endpoints.EndpointB);
    }

    [Fact]
    public void ClosedSchemaEnforcesSelfEdgePolicy()
    {
        using var world = new World();
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        world.CreateRelation(second, first, new KnownLink<int>());

        Assert.Throws<InvalidOperationException>(() => world.CreateRelation(first, first, new KnownLink<int>()));
    }

    [Fact]
    public void InvalidSchemaFailsOnFirstRead()
    {
        Assert.Throws<InvalidOperationException>(() => RelationSchema.For<KnownInvalidLink>());
    }
}

public struct RegisteredValue<T> : IComponent where T : unmanaged
{
    public T Value;
}

public struct RegisteredLink<T> : IComponent where T : unmanaged;

public struct NativeAddress<T> : IComponent where T : unmanaged
{
    public nint Address { get; set; }
}

public struct IncreaseRegisteredValues : IJobEntity
{
    public void Execute(ref RegisteredValue<int> value) => value.Value++;
}

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair, AllowSelfEdge = false)]
public struct KnownLink<T> : IComponent where T : unmanaged;

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniqueSource)]
public struct KnownInvalidLink : IComponent;
