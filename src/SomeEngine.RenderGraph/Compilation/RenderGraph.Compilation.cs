using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph;

internal readonly record struct GraphMemoryRequirements(
    ulong Size,
    ulong Alignment,
    MemoryType MemoryType,
    HeapFlags Flags);

public sealed partial class RenderGraph
{
    internal ArenaSlice<QueueType> Queues { get; set; }
    internal ArenaSlice<byte> LivenessFlags { get; set; }
    internal ArenaSlice<int> RetainingPasses { get; set; }
    internal ArenaSlice<int> ActivePassOrdinals { get; set; }
    internal ArenaSlice<BufferBoundaryIndex> BufferBoundaries { get; set; }
    internal ArenaSlice<GraphMemoryRequirements> ResourceRequirementRows { get; set; }
    internal ArenaSlice<GraphResourceUsage> BufferFinalStates { get; set; }
    internal ArenaSlice<int> TextureFinalStateOffsets { get; set; }
    internal ArenaSlice<GraphResourceUsage> TextureFinalStates { get; set; }
    internal ArenaSlice<uint> DescriptorGroups { get; set; }
    internal ArenaSlice<int> DescriptorWriteOffsets { get; set; }
    internal ArenaSlice<int> DescriptorWriteCounts { get; set; }
    internal ArenaSlice<byte> DescriptorGroupLeaders { get; set; }
    internal ArenaSlice<int> AccessIndexBuckets { get; set; }
    internal ArenaSlice<int> BindlessAccessIndexBuckets { get; set; }
    internal ArenaSlice<int> QueryIndexBuckets { get; set; }
    internal ArenaColumn<CommandBatch> CommandBatches;
    internal ArenaColumn<RuntimeCmd> CommandUnits;
    internal ArenaColumn<int> BatchDependencyRows;
    internal ArenaColumn<int> BatchRuntimeCmds;
    internal ArenaColumn<int> BatchResourceRows;
    internal ReferenceColumn<QueueCompletion> BatchExternalWaitRows;
    internal ArenaColumn<int> CommandUnitDependencyRows;
    internal ArenaColumn<int> CommandUnitPassRows;
    internal ArenaColumn<PlannedAliasingBarrier> CommandUnitAliasRows;
    internal ArenaColumn<PlannedBarrier> CommandUnitResourceBarriers;
    internal ArenaSlice<int> PassToCommandUnit { get; set; }
    internal AliasingStatistics Aliasing { get; set; }
    internal RasterStatistics Raster { get; set; }
    internal CullingStatistics Culling { get; set; }
    internal ArenaColumn<int> DependencyRows;
    internal ArenaColumn<PlannedBarrier> BeforeResourceBarriers;
    internal ArenaColumn<PlannedBarrier> AfterResourceBarriers;
    internal ArenaSlice<GraphMemoryRequirements> Heaps { get; set; }
    internal ArenaSlice<int> PlacementHeaps { get; set; }
    internal ArenaSlice<ulong> PlacementOffsets { get; set; }
    internal ArenaSlice<Extent2D> Rendering { get; set; }
    internal int MaterializedBufferViewCount { get; set; }
    internal int MaterializedTextureViewCount { get; set; }
    internal int MaterializedAccelerationStructureCount { get; set; }
    internal const byte PassLiveFlag = 1 << 0;
    internal const byte PassRootFlag = 1 << 1;
    internal const byte ResourceLiveFlag = 1 << 0;
    internal const byte ResourceWrittenFlag = 1 << 1;
    internal const byte ViewLiveFlag = 1 << 0;
    internal const byte ViewMaterializedFlag = 1 << 1;

    internal unsafe bool IsPassLive(int pass) =>
        (LivenessFlags.DangerousPointer[pass] & PassLiveFlag) != 0;
    internal Extent2D GetExtent2D(int pass) =>
        Rendering.IsEmpty ? default : Rendering[pass];
    internal unsafe bool IsPassRoot(int pass) =>
        (LivenessFlags.DangerousPointer[pass] & PassRootFlag) != 0;
    internal unsafe void MarkPassLive(int pass) =>
        LivenessFlags.DangerousPointer[pass] |= PassLiveFlag;
    internal unsafe bool MarkPassRoot(int pass)
    {
        ref byte flags = ref LivenessFlags.DangerousPointer[pass];
        bool added = (flags & PassRootFlag) == 0;
        flags |= PassRootFlag;
        return added;
    }
    internal unsafe bool MarkResourceLive(int resource)
    {
        ref byte flags =
            ref LivenessFlags.DangerousPointer[Passes.Length + resource];
        bool added = (flags & ResourceLiveFlag) == 0;
        flags |= ResourceLiveFlag;
        return added;
    }
    internal unsafe void MarkResourceWritten(int resource) =>
        LivenessFlags.DangerousPointer[Passes.Length + resource] |=
            ResourceWrittenFlag;
    internal unsafe bool IsBufferViewLive(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + view] &
         ViewLiveFlag) != 0;
    internal unsafe bool IsBufferViewMaterialized(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + view] &
         ViewMaterializedFlag) != 0;
    internal unsafe bool MarkBufferView(int view, bool materialized)
    {
        int index = Passes.Length + ResourceCount + view;
        byte* flags = LivenessFlags.DangerousPointer;
        bool added = (flags[index] & ViewLiveFlag) == 0;
        flags[index] |= materialized
            ? (byte)(ViewLiveFlag | ViewMaterializedFlag)
            : ViewLiveFlag;
        return added;
    }
    internal unsafe bool IsTextureViewLive(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + BufferViewCount + view] &
         ViewLiveFlag) != 0;
    internal unsafe bool IsTextureViewMaterialized(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + BufferViewCount + view] &
         ViewMaterializedFlag) != 0;
    internal unsafe bool MarkTextureView(int view, bool materialized)
    {
        int index = Passes.Length + ResourceCount + BufferViewCount + view;
        byte* flags = LivenessFlags.DangerousPointer;
        bool added = (flags[index] & ViewLiveFlag) == 0;
        flags[index] |= materialized
            ? (byte)(ViewLiveFlag | ViewMaterializedFlag)
            : ViewLiveFlag;
        return added;
    }
    internal unsafe bool IsAccelerationStructureLive(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + BufferViewCount +
             TextureViewCount + view] &
         ViewLiveFlag) != 0;
    internal unsafe bool IsAccelerationStructureMaterialized(int view) =>
        (LivenessFlags.DangerousPointer[
             Passes.Length + ResourceCount + BufferViewCount +
             TextureViewCount + view] &
         ViewMaterializedFlag) != 0;
    internal unsafe bool MarkAccelerationStructure(int view, bool materialized)
    {
        int index = Passes.Length + ResourceCount + BufferViewCount + TextureViewCount + view;
        byte* flags = LivenessFlags.DangerousPointer;
        bool added = (flags[index] & ViewLiveFlag) == 0;
        flags[index] |= materialized
            ? (byte)(ViewLiveFlag | ViewMaterializedFlag)
            : ViewLiveFlag;
        return added;
    }
}

internal readonly record struct PlannedBarrier(
    int Resource,
    GraphResourceUsage Before,
    GraphResourceUsage After,
    GraphBarrierKind Kind,
    QueueType OtherQueue,
    TextureSubresourceRange TextureRange,
    bool UsesPlacementInitialState)
{
    internal bool IsTransition =>
        Before != After || UsesPlacementInitialState || Kind != GraphBarrierKind.Resource;
    internal bool IsTexture => TextureRange.Aspects != TextureAspects.None;

    internal static PlannedBarrier BufferTransition(
        int resource,
        GraphResourceUsage before,
        GraphResourceUsage after,
        bool usesPlacementInitialState = false) =>
        new(
            resource,
            before,
            after,
            GraphBarrierKind.Resource,
            default,
            default,
            usesPlacementInitialState);

    internal static PlannedBarrier TextureTransition(
        int resource,
        GraphResourceUsage before,
        GraphResourceUsage after,
        in TextureSubresourceRange range,
        bool usesPlacementInitialState = false) =>
        new(
            resource,
            before,
            after,
            GraphBarrierKind.Resource,
            default,
            range,
            usesPlacementInitialState);

    internal static PlannedBarrier BufferUnorderedAccess(int resource) =>
        new(
            resource,
            GraphResourceUsage.UnorderedAccess,
            GraphResourceUsage.UnorderedAccess,
            GraphBarrierKind.Resource,
            default,
            default,
            false);

    internal static PlannedBarrier TextureUnorderedAccess(
        int resource,
        in TextureSubresourceRange range) =>
        new(
            resource,
            GraphResourceUsage.UnorderedAccess,
            GraphResourceUsage.UnorderedAccess,
            GraphBarrierKind.Resource,
            default,
            range,
            false);

    internal PlannedBarrier AsQueueRelease(QueueType destinationQueue) =>
        Kind == GraphBarrierKind.Resource && !UsesPlacementInitialState
            ? this with
            {
                Kind = GraphBarrierKind.QueueRelease,
                OtherQueue = destinationQueue,
            }
            : throw new InvalidOperationException(
                "Only a resource transition can release queue ownership.");

    internal PlannedBarrier AsQueueAcquire(QueueType sourceQueue) =>
        Kind == GraphBarrierKind.Resource && !UsesPlacementInitialState
            ? this with
            {
                Kind = GraphBarrierKind.QueueAcquire,
                OtherQueue = sourceQueue,
            }
            : throw new InvalidOperationException(
                "Only a resource transition can acquire queue ownership.");
}

internal readonly record struct Extent2D(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct CommandBatch(
    QueueType Queue,
    int DependencyOffset,
    int DependencyCount,
    int CommandUnitOffset,
    int CommandUnitCount,
    int ResourceOffset,
    int ResourceCount,
    int ExternalWaitOffset,
    int ExternalWaitCount);

internal readonly record struct RuntimeCmd(
    QueueType Queue,
    int CmdId,
    int PassOffset,
    int PassCount,
    int PayloadOrdinal,
    int SortPass,
    int StableOrdinal,
    int CreationOrdinal,
    int AliasOffset,
    int AliasCount,
    int BarrierOffset,
    int BarrierCount,
    int DependencyOffset = 0,
    int DependencyCount = 0)
{
    internal const int StandaloneCmdId = 0;
    internal const int RasterScopeCmdId = 1;
    internal const int AliasingBarrierCmdId = 2;
    internal const int BarrierCmdId = 3;

    internal string Name => CmdId switch
    {
        StandaloneCmdId => "Standalone",
        RasterScopeCmdId => "RasterScope",
        AliasingBarrierCmdId => "PlannedAliasingBarrier",
        BarrierCmdId => "Barrier",
        _ => throw new InvalidOperationException($"Unknown runtime command id {CmdId}."),
    };
}

internal readonly record struct PlannedAliasingBarrier(
    int BeforeResource,
    int AfterResource,
    ArenaSlice<int> EndPasses,
    ArenaSlice<int> StartPasses);

internal readonly record struct AliasingStatistics(
    bool Enabled,
    ulong LogicalRequestedBytes,
    ulong NonAliasedPlacedBytes,
    ulong PlannedHeapBytes,
    ulong AliasSavingsBytes,
    int IntervalCount,
    int AliasBarrierCount);

internal readonly record struct CullingStatistics(
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
