using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RootComponentContractTests : JobTest
{
    [Fact]
    public void RootOnlyComponent_WorksWithComponentJobAccessAndJobCommandBuffer()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = CreateJobWorld();

        Assert.NotEqual(
            default(JobResourceAccess),
            ((WorldJobs)world.Execution!).Read<RootOnlyJobValue>());
        Assert.NotEmpty(((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<RootOnlyJobValue>()));

        using var commands = new CommandBatch(world, 1);
        Jobs.Schedule(new RootOnlyCommandProducer(commands[0])).Complete();
        Jobs.Schedule(new PlayCommands(commands), world.Execution!.StructureWrite).Complete();

        Entity entity = default;
        int rowCount = 0;
        Query query = world.Query(new QueryDescription().WithStored<RootOnlyJobValue>());
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
                foreach (Entity value in chunk.Entities) { entity = value; rowCount++; }
        Assert.Equal(1, rowCount);
        Assert.Equal(21, world.ReadComponentData<RootOnlyJobValue>(entity).Value);
    }

    [Fact]
    public void GeneratedJobEntity_AcceptsRootOnlyComponentAtCompileAndRuntime()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = CreateJobWorld();
        Entity entity = world.Create(new RootOnlyGeneratedValue { Value = 3 });

        new RootOnlyGeneratedJob().Schedule(world).Complete();
        Assert.Equal(4, world.ReadComponentData<RootOnlyGeneratedValue>(entity).Value);

        new RootOnlyGeneratedJob().ScheduleParallel(world).Complete();
        Assert.Equal(5, world.ReadComponentData<RootOnlyGeneratedValue>(entity).Value);
    }

    [Fact]
    public void RootComponentMarkerIsAcceptedBySystemsAccess()
    {
        using var world = CreateJobWorld();
        Assert.NotEqual(
            default(JobResourceAccess),
            ((WorldJobs)world.Execution!).Read<PlainJobValue>());
    }

    private readonly struct RootOnlyCommandProducer(CommandBuffer commands) : IJob
    {
        public void Execute()
        {
            DeferredEntity entity = commands.Create();
            commands.Add(entity, new RootOnlyJobValue { Value = 21 });
        }
    }
    private readonly struct PlayCommands(CommandBatch commands) : IJob
    {
        public void Execute() => commands.Playback();
    }

    private struct RootOnlyJobValue : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct PlainJobValue : global::SomeEngine.ECS.IComponent
    {
        public int Value { get; init; }
    }

}

public struct RootOnlyGeneratedValue : global::SomeEngine.ECS.IComponent
{
    public int Value;
}

public partial struct RootOnlyGeneratedJob : IJobEntity
{
    public void Execute(ref RootOnlyGeneratedValue value) => value.Value++;
}
