using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class GeneratedMetadataTests
{
    [Fact]
    public void RegisteredSchemaAndStorageDriveOrdinaryEdgeAndQueryJobAccess()
    {
        RelationSchema.Register<RegisteredLink<int>>(
            new RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair, false));
        GeneratedEcsTypes.RegisterJobStorage<RegisteredValue<int>>(true);
        GeneratedEcsTypes.RegisterJobStorage<RelationEndpoints<RegisteredLink<int>>>(true);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        var worldJobs = new WorldJobs(jobs);
        using var world = new World(execution: worldJobs);
        var source = world.Create(new RegisteredValue<int> { Value = 4 });
        var target = world.Create();
        var edge = world.CreateRelation(source, target, new RegisteredLink<int>());
        Query values = world.Query(new QueryDescription().WithStored<RegisteredValue<int>>());
        JobResourceAccess[] accesses = worldJobs.CollectReadAccess(values);
        var observed = new List<(Entity, int)>();
        var job = new ReadRegisteredEdge(world, source, edge, observed);
        jobs.Schedule(job, accesses).Complete();

        Assert.Equal(new[] { (target, 4) }, observed);
        Assert.Throws<InvalidOperationException>(() => world.CreateRelation(source, target, new RegisteredLink<int>()));
    }

    private readonly struct ReadRegisteredEdge(World world, Entity source, RelationEdge<RegisteredLink<int>> edge,
        List<(Entity, int)> observed) : IJob
    {
        public void Execute() => observed.Add((world.Read<RelationEndpoints<RegisteredLink<int>>>(edge.Entity).Target,
            world.Read<RegisteredValue<int>>(source).Value));
    }

    [Fact]
    public void ClosedComponentsUseOrdinaryAndGeneratedJobStorageChecks()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        var worldJobs = new WorldJobs(jobs);
        using var world = new World(execution: worldJobs);
        var entity = world.Create(new RegisteredValue<int> { Value = 4 });

        new IncreaseRegisteredValues().Schedule(world).Complete();

        Assert.Equal(5, world.ReadValue<RegisteredValue<int>>(entity).Value);
        Assert.Throws<InvalidOperationException>(() => worldJobs.Read<NativeAddress<int>>());
        Query query = world.Query(new QueryDescription().WithStored<NativeAddress<int>>());
        Assert.Throws<InvalidOperationException>(() => worldJobs.CollectReadAccess(query));
    }

    [Fact]
    public void ClosedSchemaPreservesUndirectedEndpointRoles()
    {
        using var world = new World();
        var first = world.Create();
        var second = world.Create();

        var edge = world.CreateRelation(second, first, new KnownLink<int>());
        var endpoints = world.ReadValue<RelationEndpoints<KnownLink<int>>>(edge.Entity);

        Assert.Equal(second, endpoints.Source);
        Assert.Equal(first, endpoints.Target);
    }

    [Fact]
    public void ClosedSchemaEnforcesSelfEdgePolicy()
    {
        using var world = new World();
        var first = world.Create();
        var second = world.Create();
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

public partial struct IncreaseRegisteredValues : IJobEntity
{
    public void Execute(ref RegisteredValue<int> value) => value.Value++;
}

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair, AllowSelfEdge = false)]
public struct KnownLink<T> : IComponent where T : unmanaged;

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniqueSource)]
public struct KnownInvalidLink : IComponent;
