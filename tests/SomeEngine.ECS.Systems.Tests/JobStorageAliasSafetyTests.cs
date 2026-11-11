using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed partial class JobStorageAliasSafetyTests : JobTest
{
    [Fact]
    public void ReadDeclarationsRejectReferenceBearingTableBufferSparseAndSharedStorage()
    {
        World world = CreateJobWorld();
        var jobs = (WorldJobs)world.Execution!;
        Assert.Throws<InvalidOperationException>(() => jobs.Read<ReferenceTable>());
        Assert.Throws<InvalidOperationException>(() => jobs.Read<Buffer<ReferenceElement>>());
        Assert.Throws<InvalidOperationException>(() => jobs.CollectAccess(new SystemAccesses().ReadSparse<ReferenceSparse>()));
        Assert.Throws<InvalidOperationException>(() => jobs.Read<AliasShared>());
    }

    [Fact]
    public void TypedSchedulingRejectsAliasedStorageBeforeTheJobRuns()
    {
        World world = CreateJobWorld();
        var jobs = (WorldJobs)world.Execution!;
        var counter = new Counter();
        var action = new Count(counter);
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(action, [.. jobs.StructureRead, jobs.Read<ReferenceTable>()]));
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(action, jobs.CollectAccess(new SystemAccesses().ReadRandom<Buffer<ReferenceElement>>())));
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(action, jobs.CollectAccess(new SystemAccesses().ReadSparse<ReferenceSparse>())));
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(action, [.. jobs.StructureRead, jobs.Read<AliasShared>()]));
        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public void NativeSizedHandlesRequireConcreteResourceAccess()
    {
        World world = CreateJobWorld();
        var jobs = (WorldJobs)world.Execution!;
        Assert.Throws<InvalidOperationException>(() => jobs.Read<NativeHandleTable>());
        Assert.Throws<InvalidOperationException>(() => jobs.Read<NestedNativeHandleTable>());
        Assert.Throws<InvalidOperationException>(() => jobs.CollectReadAccess(world.Query<NativeHandleTable>()));
    }

    [Fact]
    public void RecursivelyIndependentStorageSupportsTypedScheduling()
    {
        World world = CreateJobWorld();
        var jobs = (WorldJobs)world.Execution!;
        var counter = new Counter();
        var action = new Count(counter);
        Jobs.Schedule(action, [.. jobs.StructureRead, jobs.Read<SafeTable>()]).Complete();
        Jobs.Schedule(action, jobs.CollectAccess(new SystemAccesses().ReadRandom<Buffer<SafeElement>>())).Complete();
        Jobs.Schedule(action, jobs.CollectAccess(new SystemAccesses().ReadSparse<SafeSparse>())).Complete();
        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void SharedAliasesAreRejectedWhenAJobBorrowsTheirValuesAndPresenceRemainsReadable()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new AliasShared { Value = "scene" }, new SafeShared { Value = 17 });
        Assert.Equal("scene", world.ReadValue<AliasShared>(entity).Value);
        var alias = new TextResult();
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new ReadAlias(world, entity, alias), world.Execution!.StructureRead).Complete());
        Assert.Null(alias.Value);
        Assert.Equal("scene", world.ReadValue<AliasShared>(entity).Value);
        var presence = new PresenceResult();
        Jobs.Schedule(new ReadPresence(world, entity, presence), world.Execution!.StructureRead).Complete();
        Assert.True(presence.Value);
        var value = new Counter();
        Jobs.Schedule(new ReadSafe(world, entity, value), world.Execution.StructureRead).Complete();
        Assert.Equal(17, value.Value);
    }

    [Fact]
    public void SharedReadsRequireTheirOwnWorldsStructurePermission()
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        Entity entity = world.Create(new SafeShared { Value = 17 });
        var result = new Counter();
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new ReadSafe(world, entity, result), other.Execution!.StructureRead).Complete());
        Assert.Equal(0, result.Value);
        Jobs.Schedule(new ReadSafe(world, entity, result), world.Execution!.StructureRead).Complete();
        Assert.Equal(17, result.Value);
    }

    [Theory]
    [InlineData(SharedFilterPath.Rows)]
    [InlineData(SharedFilterPath.Chunks)]
    public void SharedFilteredValueReadsRequireTheSelectedWorld(SharedFilterPath path)
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        var value = new SafeShared { Value = 17 };
        world.Create(value);
        SharedId<SafeShared> id = world.InternShared(value);
        Query query = world.Query(new QueryDescription().WithShared(id));
        var count = new Counter();
        var action = new ReadSharedRows<SafeShared>(query, path, count);
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(action, other.Execution!.StructureRead).Complete());
        Assert.Equal(0, count.Value);
        Jobs.Schedule(action, world.Execution!.CollectReadAccess(query)).Complete();
        Assert.Equal(1, count.Value);
    }

    [Theory]
    [InlineData(SharedFilterPath.Rows)]
    [InlineData(SharedFilterPath.Chunks)]
    public void SharedFilteredValueReadsRejectAliasesInAnOrdinaryJob(SharedFilterPath path)
    {
        World world = CreateJobWorld();
        var value = new AliasShared { Value = "scene" };
        world.Create(value);
        SharedId<AliasShared> id = world.InternShared(value);
        Query query = world.Query(new QueryDescription().WithShared(id));
        int synchronous = 0;
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
                if (path == SharedFilterPath.Rows)
                {
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.Shared<AliasShared>(row).Value == "scene") synchronous++;
                }
                else
                {
                    SharedReadOnlySpan<AliasShared> values = chunk.ReadShared<AliasShared>();
                    for (int row = 0; row < values.Length; row++)
                        if (values[row].Value == "scene") synchronous++;
                }
        Assert.Equal(1, synchronous);
        var count = new Counter();
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new ReadSharedRows<AliasShared>(query, path, count), world.Execution!.StructureRead).Complete());
        Assert.Equal(0, count.Value);
        using QueryScope after = query.OpenRead();
        foreach (QueryChunk chunk in after)
        {
            SharedReadOnlySpan<AliasShared> values = chunk.ReadShared<AliasShared>();
            for (int row = 0; row < values.Length; row++) Assert.Equal("scene", values[row].Value);
        }
    }

    private sealed class Counter { internal int Value; }
    private sealed class TextResult { internal string? Value; }
    private sealed class PresenceResult { internal bool Value; }
    private readonly struct Count(Counter counter) : IJob { public void Execute() => counter.Value++; }
    private readonly struct ReadAlias(World world, Entity entity, TextResult result) : IJob
    { public void Execute() => result.Value = world.ReadValue<AliasShared>(entity).Value; }
    private readonly struct ReadPresence(World world, Entity entity, PresenceResult result) : IJob
    { public void Execute() => result.Value = world.Has<AliasShared>(entity); }
    private readonly struct ReadSafe(World world, Entity entity, Counter result) : IJob
    { public void Execute() => result.Value = world.ReadValue<SafeShared>(entity).Value; }
    private readonly struct ReadSharedRows<T>(Query query, SharedFilterPath path, Counter count) : IJob where T : struct, ISharedComponent
    {
        public void Execute()
        {
            using QueryScope rows = query.OpenRead();
            foreach (QueryChunk chunk in rows)
                if (path == SharedFilterPath.Rows)
                {
                    for (int row = 0; row < chunk.Count; row++) { _ = chunk.Shared<T>(row); count.Value++; }
                }
                else
                {
                    SharedReadOnlySpan<T> values = chunk.ReadShared<T>();
                    for (int row = 0; row < values.Length; row++) { _ = values[row]; count.Value++; }
                }
        }
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

}
