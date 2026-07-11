namespace SomeEngine.RenderGraph;

using System.Runtime.CompilerServices;

internal sealed class CompiledGraph
{
    public CompiledGraph(
        QueueType[] queues,
        int[] activePassOrdinals,
        bool[] rootPasses,
        int[] retainingPasses,
        bool[] liveResources,
        bool[] liveBufferViews,
        bool[] liveTextureViews,
        CompiledExecutionBatch[] executionBatches,
        CompiledRecordUnit[] recordUnits,
        int[] passToRecordUnit,
        CompiledAliasingStatistics aliasing,
        CompiledRasterStatistics raster,
        CompiledCullingStatistics culling,
        int[][] dependencies,
        BarrierTemplate[][] beforeBarriers,
        BarrierTemplate[][] afterBarriers,
        CompiledHeap[] heaps,
        CompiledPlacement[] placements,
        CompiledRendering?[] rendering,
        bool optimized)
    {
        Queues = queues;
        ActivePassOrdinals = activePassOrdinals;
        RootPasses = rootPasses;
        RetainingPasses = retainingPasses;
        LiveResources = liveResources;
        LiveBufferViews = liveBufferViews;
        LiveTextureViews = liveTextureViews;
        ExecutionBatches = executionBatches;
        RecordUnits = recordUnits;
        PassToRecordUnit = passToRecordUnit;
        Aliasing = aliasing;
        Raster = raster;
        Culling = culling;
        Dependencies = dependencies;
        BeforeBarriers = beforeBarriers;
        AfterBarriers = afterBarriers;
        Heaps = heaps;
        Placements = placements;
        Rendering = rendering;
        Optimized = optimized;
        EstimatedRetainedBytes = EstimateRetainedBytes(
            queues,
            activePassOrdinals,
            rootPasses,
            retainingPasses,
            liveResources,
            liveBufferViews,
            liveTextureViews,
            executionBatches,
            recordUnits,
            passToRecordUnit,
            raster,
            dependencies,
            beforeBarriers,
            afterBarriers,
            heaps,
            placements,
            rendering);
    }

    public QueueType[] Queues { get; }
    public int[] ActivePassOrdinals { get; }
    public bool[] RootPasses { get; }
    public int[] RetainingPasses { get; }
    public bool[] LiveResources { get; }
    public bool[] LiveBufferViews { get; }
    public bool[] LiveTextureViews { get; }
    public CompiledExecutionBatch[] ExecutionBatches { get; }
    public CompiledRecordUnit[] RecordUnits { get; }
    public int[] PassToRecordUnit { get; }
    public CompiledAliasingStatistics Aliasing { get; }
    public CompiledRasterStatistics Raster { get; }
    public CompiledCullingStatistics Culling { get; }
    public int[][] Dependencies { get; }
    public BarrierTemplate[][] BeforeBarriers { get; }
    public BarrierTemplate[][] AfterBarriers { get; }
    public CompiledHeap[] Heaps { get; }
    public CompiledPlacement[] Placements { get; }
    public CompiledRendering?[] Rendering { get; }
    public bool Optimized { get; }
    /// <summary>
    /// Deterministic payload accounting used by the transparent cache. It includes array payloads
    /// and stable per-array/object overhead estimates, not runtime-specific GC bookkeeping.
    /// </summary>
    public long EstimatedRetainedBytes { get; }

    private static long EstimateRetainedBytes(
        QueueType[] queues,
        int[] activePassOrdinals,
        bool[] rootPasses,
        int[] retainingPasses,
        bool[] liveResources,
        bool[] liveBufferViews,
        bool[] liveTextureViews,
        CompiledExecutionBatch[] executionBatches,
        CompiledRecordUnit[] recordUnits,
        int[] passToRecordUnit,
        CompiledRasterStatistics raster,
        int[][] dependencies,
        BarrierTemplate[][] beforeBarriers,
        BarrierTemplate[][] afterBarriers,
        CompiledHeap[] heaps,
        CompiledPlacement[] placements,
        CompiledRendering?[] rendering)
    {
        const long objectOverhead = 32;
        long bytes = checked(
            objectOverhead +
            Unsafe.SizeOf<CompiledAliasingStatistics>() +
            Unsafe.SizeOf<CompiledCullingStatistics>());
        bytes = checked(bytes + Unsafe.SizeOf<CompiledRasterStatistics>() + EstimateArray(raster.BreakReasonCounts));
        bytes = checked(bytes + EstimateArray(queues));
        bytes = checked(bytes + EstimateArray(activePassOrdinals));
        bytes = checked(bytes + EstimateArray(rootPasses));
        bytes = checked(bytes + EstimateArray(retainingPasses));
        bytes = checked(bytes + EstimateArray(liveResources));
        bytes = checked(bytes + EstimateArray(liveBufferViews));
        bytes = checked(bytes + EstimateArray(liveTextureViews));
        bytes = checked(bytes + EstimateArray(passToRecordUnit));
        bytes = checked(bytes + EstimateExecutionBatches(executionBatches));
        bytes = checked(bytes + EstimateRecordUnits(recordUnits));
        bytes = checked(bytes + EstimateJagged(dependencies));
        bytes = checked(bytes + EstimateJagged(beforeBarriers));
        bytes = checked(bytes + EstimateJagged(afterBarriers));
        bytes = checked(bytes + EstimateArray(heaps));
        bytes = checked(bytes + EstimateArray(placements));
        bytes = checked(bytes + EstimateArray(rendering));
        return bytes;
    }

    private static long EstimateExecutionBatches(CompiledExecutionBatch[] batches)
    {
        long bytes = EstimateArray(batches);
        foreach (CompiledExecutionBatch batch in batches)
        {
            bytes = checked(bytes + EstimateArray(batch.Dependencies));
            bytes = checked(bytes + EstimateArray(batch.RecordUnits));
        }
        return bytes;
    }

    private static long EstimateRecordUnits(CompiledRecordUnit[] units)
    {
        long bytes = EstimateArray(units);
        foreach (CompiledRecordUnit unit in units)
        {
            bytes = checked(bytes + EstimateArray(unit.LogicalPassOrdinals));
            bytes = checked(bytes + EstimateArray(unit.AliasAcquires));
            bytes = checked(bytes + EstimateArray(unit.InternalBarriers));
        }
        return bytes;
    }

    private static long EstimateJagged<T>(T[][] values)
    {
        long bytes = EstimateReferenceArray(values.Length);
        foreach (T[] value in values) bytes = checked(bytes + EstimateArray(value));
        return bytes;
    }

    private static long EstimateArray<T>(T[] values) =>
        checked(24L + (long)values.Length * Unsafe.SizeOf<T>());

    private static long EstimateReferenceArray(int length) => checked(24L + (long)length * IntPtr.Size);
}

internal readonly record struct BarrierTemplate(
    BarrierKind Kind,
    int Resource,
    ResourceState Before,
    ResourceState After,
    TextureSubresourceRange TextureRange,
    int AliasingBefore = -1);

internal readonly record struct CompiledHeap(
    ulong Size,
    MemoryType MemoryType,
    ResourceHeapClass ResourceClass,
    ulong CompatibilityClass);

internal readonly record struct CompiledPlacement(int Heap, ulong Offset)
{
    public bool IsPlaced => Heap >= 0;
}

internal readonly record struct CompiledRendering(int Width, int Height);

internal readonly record struct CompiledExecutionBatch(
    QueueType Queue,
    int[] Dependencies,
    int[] RecordUnits);

internal enum CompiledRecordUnitKind : byte
{
    Standalone,
    RasterScope,
    AliasAcquire,
    InternalBarriers,
}

internal readonly record struct CompiledRecordUnit(
    QueueType Queue,
    CompiledRecordUnitKind Kind,
    int[] LogicalPassOrdinals,
    CompiledAliasAcquire[] AliasAcquires,
    BarrierTemplate[] InternalBarriers);

internal readonly record struct CompiledAliasAcquire(int BeforeResource, int AfterResource);

internal readonly record struct CompiledAliasingStatistics(
    bool Enabled,
    ulong LogicalRequestedBytes,
    ulong NonAliasedPlacedBytes,
    ulong PlannedHeapBytes,
    ulong AliasSavingsBytes,
    int AliasSlotCount,
    int AliasAcquireCount);

internal readonly record struct CompiledCullingStatistics(
    int DeclaredPasses,
    int LivePasses,
    int CulledPasses,
    int DeclaredResources,
    int LiveResources,
    int CulledResources,
    int DeclaredViews,
    int LiveViews,
    int CulledViews,
    ulong CulledTransientBytes,
    int ImportedWriteRoots);
