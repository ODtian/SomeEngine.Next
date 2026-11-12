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
        var world = new World(execution: new WorldJobs(JobSystem.Default));
        Entity entity = world.Create(new VersionedValue { Value = 1 });
        world.SetSparse(entity, new SparseValue { Value = 2 });
        ulong tableVersion = world.ComponentDataWriteVersion<VersionedValue>(entity);
        ulong sparseVersion = world.ComponentDataWriteVersion<SparseValue>(entity);

        if (parallel)
            new SystemRunner(world, rangeRows: 1).Schedule(new IncrementValuesJob(), dependency: default, parallel: true).Complete();
        else
            new IncrementValuesJob().Schedule(world).Complete();

        Assert.NotEqual(tableVersion, world.ComponentDataWriteVersion<VersionedValue>(entity));
        Assert.NotEqual(sparseVersion, world.ComponentDataWriteVersion<SparseValue>(entity));
        Assert.Equal(2, world.ReadComponentData<VersionedValue>(entity).Value);
        Assert.Equal(3, world.ReadComponentData<SparseValue>(entity).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyJobPreservesTableAndSparseVersions(bool parallel)
    {
        using var jobs = new JobWorkers();
        var world = new World(execution: new WorldJobs(JobSystem.Default));
        var entity = world.Create(new VersionedValue { Value = 1 });
        world.SetSparse(entity, new SparseValue { Value = 2 });
        ulong tableVersion = world.ComponentDataWriteVersion<VersionedValue>(entity);
        ulong sparseVersion = world.ComponentDataWriteVersion<SparseValue>(entity);

        if (parallel)
            new ReadValuesJob().ScheduleParallel(world).Complete();
        else
            new ReadValuesJob().Schedule(world).Complete();

        Assert.Equal(tableVersion, world.ComponentDataWriteVersion<VersionedValue>(entity));
        Assert.Equal(sparseVersion, world.ComponentDataWriteVersion<SparseValue>(entity));
    }

    [Fact]
    public void SparseCallbackJobAdvancesBorrowedVersions()
    {
        using var jobs = new JobWorkers();
        var world = new World(execution: new WorldJobs(JobSystem.Default));
        var entity = world.Create();
        world.SetSparse(entity, new SparseValue { Value = 1 });
        ulong version = world.ComponentDataWriteVersion<SparseValue>(entity);

        world.Execution!.Scheduler!.Schedule(new IncrementSparseValuesJob(world),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseValue>())).Complete();

        Assert.NotEqual(version, world.ComponentDataWriteVersion<SparseValue>(entity));
        Assert.Equal(2, world.ReadComponentData<SparseValue>(entity).Value);
    }

    [Fact]
    public void ParallelJobPreservesEveryWriteAfterStructuralReplay()
    {
        using var jobs = new JobWorkers();
        var world = new World(execution: new WorldJobs(JobSystem.Default));
        var entities = new Entity[256];
        var versions = new ulong[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new VersionedValue { Value = i });
            world.SetSparse(entities[i], new SparseValue { Value = i });
            versions[i] = world.ComponentDataWriteVersion<SparseValue>(entities[i]);
        }
        using (var commands = new CommandBuffer(world))
        {
            commands.Add(entities[0], default(Selected));
            commands.Playback();
        }

        new SystemRunner(world, rangeRows: 1).Schedule(new IncrementValuesJob(), dependency: default, parallel: true).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            Assert.NotEqual(versions[i], world.ComponentDataWriteVersion<SparseValue>(entities[i]));
            Assert.Equal(i + 1, world.ReadComponentData<VersionedValue>(entities[i]).Value);
            Assert.Equal(i + 1, world.ReadComponentData<SparseValue>(entities[i]).Value);
        }
    }

    private sealed class JobWorkers : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobWorkers()
        {
            JobSystem.Initialize(new JobConfiguration
            {
                WorkerCount = 2,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobConfiguration
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
        }
    }

    private readonly struct IncrementSparseValuesJob(World world) : IJob
    {
        public void Execute()
        {
            using var values = world.OpenSparseWrite<SparseValue>();
            foreach (ref SparseValue value in values.Values) value.Value++;
        }
    }
}

public struct VersionedValue : SomeEngine.ECS.IComponent, ITrackedComponent
{
    public int Value;
}

public struct SparseValue : ISparseComponent, ITrackedComponent
{
    public int Value;
}

public struct Selected : IComponent;

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
