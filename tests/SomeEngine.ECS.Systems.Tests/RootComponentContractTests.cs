using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RootComponentContractTests : JobTest
{
    [Fact]
    public void RootOnlyComponent_WorksWithComponentJobAccessAndJobCommandBuffer()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = CreateJobWorld();

        Assert.NotEqual(
            default(JobResourceAccess),
            ComponentJobAccess<RootOnlyJobValue>.Read(world));
        Assert.NotEqual(
            default(JobResourceAccess),
            ComponentJobAccess<RootOnlyJobValue>.Write(world));

        using var commands = new JobCommandBuffer(world, producerCount: 1);
        commands.Schedule(0, new RootOnlyCommandProducer());
        commands.SchedulePlayback().Complete();

        Entity entity = default;
        int rowCount = 0;
        QueryHandle query = world.Query(world.QueryDefinition().Read<RootOnlyJobValue>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
            {
                entity = row.Entity;
                rowCount++;
            }
        });
        Assert.Equal(1, rowCount);
        Assert.Equal(21, world.Read<RootOnlyJobValue>(entity).Value);
    }

    [Fact]
    public void GeneratedJobEntity_AcceptsRootOnlyComponentAtCompileAndRuntime()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = CreateJobWorld();
        Entity entity = world.CreateEntity(new RootOnlyGeneratedValue { Value = 3 });

        GeneratedQueryAccessDescriptor descriptor =
            new RootOnlyGeneratedJob().GetGeneratedQueryAccess();
        Assert.Equal(typeof(RootOnlyGeneratedValue), descriptor.GetAccess(0).ValueType);

        new RootOnlyGeneratedJob().Schedule(world).Complete();
        Assert.Equal(4, world.Read<RootOnlyGeneratedValue>(entity).Value);

        new RootOnlyGeneratedJob().ScheduleParallel(world).Complete();
        Assert.Equal(5, world.Read<RootOnlyGeneratedValue>(entity).Value);
    }

    [Fact]
    public void CanonicalComponentContract_IsAcceptedBySystemsAccess()
    {
        using var world = CreateJobWorld();
        Assert.NotEqual(
            default(JobResourceAccess),
            ComponentJobAccess<CanonicalJobValue>.Read(world));
    }

    private readonly struct RootOnlyCommandProducer : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            var entity = commands.CreateEntity();
            commands.Add(entity, new RootOnlyJobValue { Value = 21 });
        }
    }

    private struct RootOnlyJobValue : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct CanonicalJobValue : global::SomeEngine.ECS.IComponent
    {
        public int Value { get; init; }
    }

}

public struct RootOnlyGeneratedValue : global::SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct RootOnlyGeneratedJob : IJobEntity
{
    public void Execute(ref RootOnlyGeneratedValue value) => value.Value++;
}
