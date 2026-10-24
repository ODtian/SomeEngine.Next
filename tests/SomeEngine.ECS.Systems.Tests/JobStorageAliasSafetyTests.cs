using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class JobStorageAliasSafetyTests : JobTest
{
    [Fact]
    public void ReadAccessRejectsReferenceBearingTableBufferSparseAndSharedStorage()
    {
        var world = CreateJobWorld();

        AssertAliasRejection(() => ComponentJobAccess<ReferenceTable>.Read(world));
        AssertAliasRejection(() => BufferJobAccess<ReferenceElement>.Read(world));
        AssertAliasRejection(() => SparseJobAccess<ReferenceSparse>.Read(world));
        AssertAliasRejection(() => SharedJobAccess<AliasShared>.Read(world));
    }

    [Fact]
    public void TypedScheduleRejectsAliasBearingStorageBeforeJobCallback()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var counter = new Counter();
        var job = new CountJob(counter);

        AssertAliasRejection(() => ComponentJobAccess<ReferenceTable>.ScheduleRead(world, job));
        AssertAliasRejection(() => BufferJobAccess<ReferenceElement>.ScheduleRead(world, job));
        AssertAliasRejection(() => SparseJobAccess<ReferenceSparse>.ScheduleRead(world, job));
        AssertAliasRejection(() => SharedJobAccess<AliasShared>.ScheduleRead(world, job));

        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public void NativeSizedHandleTableIsRejectedByOrdinaryAndGeneratedAccesses()
    {
        var world = CreateJobWorld();

        AssertAliasRejection(() => ComponentJobAccess<NativeHandleTable>.Read(world));
        AssertAliasRejection(() => ComponentJobAccess<NestedNativeHandleTable>.Read(world));

        QueryDefinition query = new QueryDefinitionBuilder()
            .Read<NativeHandleTable>()
            .Build();
        InvalidOperationException generated = Assert.Throws<InvalidOperationException>(() =>
            new GeneratedQueryAccessDescriptor(
                query,
                GeneratedQueryAccess.Table<NativeHandleTable>(GeneratedQueryMode.Read)));

    }

    [Fact]
    public void RecursivelyAliasFreeStorageKeepsOrdinaryTypedSchedulingAvailable()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var counter = new Counter();
        var job = new CountJob(counter);

        _ = ComponentJobAccess<SafeTable>.Read(world);
        _ = BufferJobAccess<SafeElement>.Read(world);
        _ = SparseJobAccess<SafeSparse>.Read(world);

        ComponentJobAccess<SafeTable>.ScheduleRead(world, job).Complete();
        BufferJobAccess<SafeElement>.ScheduleRead(world, job).Complete();
        SparseJobAccess<SafeSparse>.ScheduleRead(world, job).Complete();

        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void SharedValueAliasesAreRejectedOnlyWhenAJobBorrowsTheValue()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        world.AddShared(entity, new AliasShared { Value = "scene" });
        world.AddShared(entity, new SafeShared { Value = 17 });

        // Synchronous callers may still use managed shared values.
        Assert.Equal("scene", world.GetShared<AliasShared>(entity).Value);

        var aliasCapture = new StringCapture();
        JobHandle aliasRead = Jobs.Schedule(
            new ReadAliasSharedJob(world, entity, aliasCapture),
            RelationshipJobAccess.TopologyRead(world));
        InvalidOperationException aliasError = Assert.Throws<InvalidOperationException>(
            () => aliasRead.Complete());

        Assert.Null(aliasCapture.Value);
        Assert.Equal("scene", world.GetShared<AliasShared>(entity).Value);

        // HasShared observes archetype structure only and does not borrow the shared value.
        var hasCapture = new BoolCapture();
        Jobs.Schedule(
            new HasAliasSharedJob(world, entity, hasCapture),
            RelationshipJobAccess.TopologyRead(world)).Complete();
        Assert.True(hasCapture.Value);

        var safeCapture = new IntCapture();
        JobHandle missingSharedCapability = Jobs.Schedule(
            new ReadSafeSharedJob(world, entity, safeCapture),
            RelationshipJobAccess.TopologyRead(world));
        Assert.Throws<JobResourceSafetyException>(() => missingSharedCapability.Complete());
        Assert.Equal(0, safeCapture.Value);

        SharedJobAccess<SafeShared>.ScheduleRead(
            world,
            new ReadSafeSharedJob(world, entity, safeCapture)).Complete();
        Assert.Equal(17, safeCapture.Value);
    }

    [Fact]
    public void SharedReadCapabilityIsExactByComponentType()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        world.AddShared(entity, new SafeShared { Value = 17 });

        var capture = new IntCapture();
        JobResourceAccess[] wrongTypeAccesses =
        [
            SharedJobAccess<OtherSafeShared>.Read(world),
            RelationshipJobAccess.TopologyRead(world),
        ];
        JobHandle wrongType = Jobs.Schedule(
            new ReadSafeSharedJob(world, entity, capture),
            wrongTypeAccesses);

        Assert.Throws<JobResourceSafetyException>(() => wrongType.Complete());
        Assert.Equal(0, capture.Value);

        SharedJobAccess<SafeShared>.ScheduleRead(
            world,
            new ReadSafeSharedJob(world, entity, capture)).Complete();
        Assert.Equal(17, capture.Value);
    }

    [Theory]
    [InlineData(SharedFilterPath.Rows)]
    [InlineData(SharedFilterPath.Chunks)]
    public void AliasFreeDynamicSharedFilterRequiresExactSharedReadCapability(
        SharedFilterPath path)
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        var filter = new SafeShared { Value = 17 };
        world.AddShared(entity, filter);
        QueryHandle query = world.Query(
            new QueryDefinitionBuilder().Shared<SafeShared>().Build());

        var capture = new IntCapture();
        var job = new SafeDynamicSharedFilterJob(world, query, filter, path, capture);
        JobHandle topologyOnly = Jobs.Schedule(
            job,
            RelationshipJobAccess.TopologyRead(world));
        Assert.Throws<JobResourceSafetyException>(() => topologyOnly.Complete());
        Assert.Equal(0, capture.Value);

        SharedJobAccess<SafeShared>.ScheduleRead(world, job).Complete();
        Assert.Equal(1, capture.Value);
    }

    [Theory]
    [InlineData(SharedFilterPath.Rows)]
    [InlineData(SharedFilterPath.Chunks)]
    public void DynamicSharedValueFilterRejectsAliasesInsideTopologyReadJob(
        SharedFilterPath path)
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        var filter = new AliasShared { Value = "scene" };
        world.AddShared(entity, filter);
        QueryHandle query = world.Query(
            new QueryDefinitionBuilder().Shared<AliasShared>().Build());

        int synchronousMatches = 0;
        world.ExecuteQuery(
            query,
            lastSystemVersion: 0,
            cursor =>
            {
                foreach (QueryRow _ in cursor.RowsWithShared(filter))
                    synchronousMatches++;
            });
        Assert.Equal(1, synchronousMatches);

        JobHandle handle = Jobs.Schedule(
            new DynamicSharedFilterJob(world, query, filter, path),
            RelationshipJobAccess.TopologyRead(world));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => handle.Complete());

        int boundSynchronousMatches = 0;
        world.ExecuteQuery(
            query,
            lastSystemVersion: 0,
            cursor =>
            {
                foreach (QueryChunkView _ in cursor.ChunksWithShared(filter))
                    boundSynchronousMatches++;
            });
        Assert.Equal(1, boundSynchronousMatches);
    }

    private void AssertAliasRejection(Action action)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);

    }

    private sealed class Counter
    {
        internal int Value;
    }

    private sealed class StringCapture
    {
        internal string? Value;
    }

    private sealed class BoolCapture
    {
        internal bool Value;
    }

    private sealed class IntCapture
    {
        internal int Value;
    }

    private readonly struct CountJob : IJob
    {
        private readonly Counter _counter;

        internal CountJob(Counter counter)
        {
            _counter = counter;
        }

        public void Execute() => Interlocked.Increment(ref _counter.Value);
    }

    private readonly struct ReadAliasSharedJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly StringCapture _capture;

        internal ReadAliasSharedJob(World world, Entity entity, StringCapture capture)
        {
            _world = world;
            _entity = entity;
            _capture = capture;
        }

        public void Execute() =>
            _capture.Value = _world.GetShared<AliasShared>(_entity).Value;
    }

    private readonly struct HasAliasSharedJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly BoolCapture _capture;

        internal HasAliasSharedJob(World world, Entity entity, BoolCapture capture)
        {
            _world = world;
            _entity = entity;
            _capture = capture;
        }

        public void Execute() =>
            _capture.Value = _world.HasShared<AliasShared>(_entity);
    }

    private readonly struct ReadSafeSharedJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly IntCapture _capture;

        internal ReadSafeSharedJob(World world, Entity entity, IntCapture capture)
        {
            _world = world;
            _entity = entity;
            _capture = capture;
        }

        public void Execute() =>
            _capture.Value = _world.GetShared<SafeShared>(_entity).Value;
    }

    private readonly struct DynamicSharedFilterJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly AliasShared _filter;
        private readonly SharedFilterPath _path;

        internal DynamicSharedFilterJob(
            World world,
            QueryHandle query,
            in AliasShared filter,
            SharedFilterPath path)
        {
            _world = world;
            _query = query;
            _filter = filter;
            _path = path;
        }

        public void Execute()
        {
            var state = new DynamicSharedFilterState(_filter, _path);
            _world.ExecuteQuery(
                _query,
                lastSystemVersion: 0,
                ref state,
                static (QueryCursor cursor, ref DynamicSharedFilterState state) =>
                {
                    if (state.Path == SharedFilterPath.Rows)
                    {
                        foreach (QueryRow _ in cursor.RowsWithShared(state.Filter))
                        {
                        }
                        return;
                    }

                    foreach (QueryChunkView _ in cursor.ChunksWithShared(state.Filter))
                    {
                    }
                });
        }
    }

    private readonly struct SafeDynamicSharedFilterJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly SafeShared _filter;
        private readonly SharedFilterPath _path;
        private readonly IntCapture _capture;

        internal SafeDynamicSharedFilterJob(
            World world,
            QueryHandle query,
            in SafeShared filter,
            SharedFilterPath path,
            IntCapture capture)
        {
            _world = world;
            _query = query;
            _filter = filter;
            _path = path;
            _capture = capture;
        }

        public void Execute()
        {
            var state = new SafeDynamicSharedFilterState(_filter, _path, _capture);
            _world.ExecuteQuery(
                _query,
                lastSystemVersion: 0,
                ref state,
                static (QueryCursor cursor, ref SafeDynamicSharedFilterState state) =>
                {
                    if (state.Path == SharedFilterPath.Rows)
                    {
                        foreach (QueryRow _ in cursor.RowsWithShared(state.Filter))
                            Interlocked.Increment(ref state.Capture.Value);
                        return;
                    }

                    foreach (QueryChunkView chunk in cursor.ChunksWithShared(state.Filter))
                        Interlocked.Add(ref state.Capture.Value, chunk.Count);
                });
        }
    }

    private struct DynamicSharedFilterState
    {
        internal DynamicSharedFilterState(AliasShared filter, SharedFilterPath path)
        {
            Filter = filter;
            Path = path;
        }

        internal AliasShared Filter;
        internal SharedFilterPath Path;
    }

    private struct SafeDynamicSharedFilterState
    {
        internal SafeDynamicSharedFilterState(
            SafeShared filter,
            SharedFilterPath path,
            IntCapture capture)
        {
            Filter = filter;
            Path = path;
            Capture = capture;
        }

        internal SafeShared Filter;
        internal SharedFilterPath Path;
        internal IntCapture Capture;
    }

    private struct ReferenceTable : IComponent
    {
        internal object? Value { get; set; }
    }

    private struct ReferenceElement : IBufferElement
    {
        internal object? Value { get; set; }
    }

    private struct ReferenceSparse : ISparseComponent
    {
        internal object? Value { get; set; }
    }

    private struct NativeHandleTable : IComponent
    {
        internal nint Value { get; set; }
    }

    private struct NativeHandleLeaf
    {
        internal nuint Value { get; set; }
    }

    private struct NestedNativeHandleTable : IComponent
    {
        internal NativeHandleLeaf Value { get; set; }
    }

    private struct SafeLeaf
    {
        internal long Value { get; set; }
    }

    private struct SafeTable : IComponent
    {
        internal SafeLeaf Value { get; set; }
    }

    private struct SafeElement : IBufferElement
    {
        internal SafeLeaf Value { get; set; }
    }

    private struct SafeSparse : ISparseComponent
    {
        internal SafeLeaf Value { get; set; }
    }

    private struct AliasShared : ISharedComponent
    {
        internal string? Value;
    }

    private struct SafeShared : ISharedComponent
    {
        internal int Value;
    }

    private readonly struct OtherSafeShared : ISharedComponent;

    public enum SharedFilterPath : byte
    {
        Rows,
        Chunks,
    }

    private readonly struct Domain : IHierarchyDomain;

}
