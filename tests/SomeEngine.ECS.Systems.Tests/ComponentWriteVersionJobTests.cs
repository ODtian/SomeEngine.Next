using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ComponentWriteVersionJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedJobAdvancesWrittenTableAndSparseVersions(bool parallel)
    {
        using var jobs = new JobWorkers();
        var world = WorldJobs.CreateWorld();
        Entity entity = world.CreateEntity(new VersionedValue { Value = 1 });
        world.AddSparse(entity, new SparseValue { Value = 2 });
        uint tableVersion = world.WriteVersion<VersionedValue>(entity);
        uint sparseVersion = world.SparseWriteVersion<SparseValue>(entity);

        if (parallel)
            new IncrementValuesJob().ScheduleParallel(world,
                new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        else
            new IncrementValuesJob().Schedule(world).Complete();

        Assert.NotEqual(tableVersion, world.WriteVersion<VersionedValue>(entity));
        Assert.NotEqual(sparseVersion, world.SparseWriteVersion<SparseValue>(entity));
        Assert.Equal(2, world.Read<VersionedValue>(entity).Value);
        Assert.Equal(3, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyJobPreservesTableAndSparseVersions(bool parallel)
    {
        using var jobs = new JobWorkers();
        var world = WorldJobs.CreateWorld();
        var entity = world.CreateEntity(new VersionedValue { Value = 1 });
        world.AddSparse(entity, new SparseValue { Value = 2 });
        uint tableVersion = world.WriteVersion<VersionedValue>(entity);
        uint sparseVersion = world.SparseWriteVersion<SparseValue>(entity);

        if (parallel)
            new ReadValuesJob().ScheduleParallel(world).Complete();
        else
            new ReadValuesJob().Schedule(world).Complete();

        Assert.Equal(tableVersion, world.WriteVersion<VersionedValue>(entity));
        Assert.Equal(sparseVersion, world.SparseWriteVersion<SparseValue>(entity));
    }

    [Fact]
    public void SparseCallbackJobAdvancesBorrowedVersions()
    {
        using var jobs = new JobWorkers();
        var world = WorldJobs.CreateWorld();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new SparseValue { Value = 1 });
        uint version = world.SparseWriteVersion<SparseValue>(entity);

        SparseJobAccess<SparseValue>.ScheduleWrite(world,
            new IncrementSparseValuesJob(world)).Complete();

        Assert.NotEqual(version, world.SparseWriteVersion<SparseValue>(entity));
        Assert.Equal(2, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void ParallelJobPreservesEveryWriteAfterStructuralReplay()
    {
        using var jobs = new JobWorkers();
        var world = WorldJobs.CreateWorld();
        var entities = new Entity[256];
        var versions = new uint[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new VersionedValue { Value = i });
            world.AddSparse(entities[i], new SparseValue { Value = i });
            versions[i] = world.SparseWriteVersion<SparseValue>(entities[i]);
        }
        using (var commands = new CommandBuffer(world))
        {
            commands.AddTag<Selected>(entities[0]);
            commands.Playback();
        }

        new IncrementValuesJob().ScheduleParallel(world,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            Assert.NotEqual(versions[i], world.SparseWriteVersion<SparseValue>(entities[i]));
            Assert.Equal(i + 1, world.Read<VersionedValue>(entities[i]).Value);
            Assert.Equal(i + 1, world.ReadSparse<SparseValue>(entities[i]).Value);
        }
    }

    private sealed class JobWorkers : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobWorkers()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = 2,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
        }
    }

    private readonly struct IncrementSparseValuesJob(World world) : IJob
    {
        public void Execute() => world.ExecuteSparseWrite<SparseValue>(static (entities, values) =>
        {
            for (int i = 0; i < values.Length; i++)
                values[i].Value++;
        });
    }
}

public struct VersionedValue : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct SparseValue : ISparseComponent
{
    public int Value;
}

public struct Selected : ITag;

public partial struct ReadValuesJob : IJobEntity
{
    public void Execute(in VersionedValue value, in SparseValue sparse) { }
}

public partial struct IncrementValuesJob : IJobEntity
{
    public void Execute(ref VersionedValue value, ref SparseValue sparse)
    {
        value.Value++;
        sparse.Value++;
    }
}
