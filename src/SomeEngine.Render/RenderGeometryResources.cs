using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render;

/// <summary>
/// Feature-neutral geometry resources produced by Cluster. Motion, debug, RT export, streamout,
/// and other independent features consume this capability without changing Cluster shader roles.
/// </summary>
public sealed class RenderGeometryResources
{
    public RenderGeometryResources(
        GraphBufferId packedViews,
        GraphBufferId instanceData,
        GraphBufferInput instanceProperties,
        BufferRange instancePropertiesRange,
        GraphBufferId visibleClusters,
        GraphBufferId phaseOneCounts,
        GraphBufferId phaseTwoCounts,
        uint visibleClusterCapacity,
        GraphBufferId pageHeap,
        GraphBufferId bvh,
        GraphPassId completePass)
    {
        PackedViews = packedViews;
        InstanceData = instanceData;
        InstanceProperties = instanceProperties;
        InstancePropertiesRange = instancePropertiesRange;
        VisibleClusters = visibleClusters;
        PhaseOneCounts = phaseOneCounts;
        PhaseTwoCounts = phaseTwoCounts;
        VisibleClusterCapacity = visibleClusterCapacity;
        PageHeap = pageHeap;
        Bvh = bvh;
        CompletePass = completePass;
    }

    public GraphBufferId PackedViews { get; }
    public uint PackedViewStride => RenderRasterContext.ViewSizeInBytes;
    public GraphBufferId InstanceData { get; }
    public GraphBufferId InstanceWorldBounds { get; init; }
    /// <summary>Whole-frame RenderGeometryWorkFlags flags. Nonzero forbids publishing cached depth as complete.</summary>
    public GraphBufferId WorkStatus { get; init; }
    public GraphPassId BoundsReady { get; init; }
    public GraphBufferInput InstanceProperties { get; }
    public BufferRange InstancePropertiesRange { get; }
    public GraphBufferId VisibleClusters { get; }
    public GraphBufferId DeformCache { get; init; }
    public GraphBufferId DeformRegions { get; init; }
    public GraphBufferId PhaseOneCounts { get; }
    public GraphBufferId PhaseTwoCounts { get; }
    public uint VisibleClusterCapacity { get; }
    public GraphBufferId PageHeap { get; }
    public GraphBufferId Bvh { get; }
    /// <summary>Feature-neutral fence after the geometry visibility/raster work is complete.</summary>
    public GraphPassId CompletePass { get; }

    public bool IsValid =>
        PackedViews != default &&
        InstanceData != default &&
        InstanceProperties != default &&
        VisibleClusters != default &&
        PhaseOneCounts != default &&
        PhaseTwoCounts != default &&
        VisibleClusterCapacity != 0 &&
        PageHeap != default &&
        Bvh != default &&
        CompletePass != default &&
        InstancePropertiesRange.Size != 0;

    /// <summary>
    /// Connects the page heap descriptor. ReadPageHeap supplies each frame's resident ranges.
    /// </summary>
    public void BindPageHeap(
        RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync,
        string? label = null)
    {
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(PageHeap, label: label ?? "Cluster geometry page heap")));
    }

    public void ReadPageHeap(ref RenderGraphFrame frame, ref PassDefinition pass, PipelineSync sync) =>
        pass.ReadRanges(PageHeap, GetFrameRanges(ref frame).ResidentPageReads, sync, ResourceAccess.ShaderResource);

    public RenderGeometryFrameRanges GetFrameRanges(ref RenderGraphFrame frame) =>
        frame.GetFrameData(this, static () => new RenderGeometryFrameRanges());

    public void SetFrameRanges(ref RenderGraphFrame frame, BufferRange[] pageRanges, BufferRange[] bvhRanges)
    {
        RenderGeometryFrameRanges ranges = GetFrameRanges(ref frame);
        ranges.ResidentPageReads = new BufferRangeSet(pageRanges);
        ranges.BvhReadRanges = new BufferRangeSet(bvhRanges);
    }
}

public sealed class RenderGeometryFrameRanges
{
    public BufferRangeSet ResidentPageReads { get; internal set; } = BufferRangeSet.Empty;
    public BufferRangeSet BvhReadRanges { get; internal set; } = BufferRangeSet.Empty;
    public bool HasBvhData => BvhReadRanges.Count != 0;
}
