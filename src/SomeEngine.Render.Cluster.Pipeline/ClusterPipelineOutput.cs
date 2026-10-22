using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Fixed geometry-output shape for one independently instantiable Cluster pipeline. It contains
/// no swapchain, presentation, lighting, temporal, or post-processing policy.
/// </summary>
public readonly record struct ClusterPipelineOutputDescription
{
    public ClusterPipelineOutputDescription(int width, int height, uint viewCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        if (checked((ulong)width * (ulong)height * viewCount) > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(viewCount),
                "The combined view pixels must fit the 32-bit shading counters and offsets.");
        Width = width;
        Height = height;
        ViewCount = viewCount;
    }

    public int Width { get; }

    public int Height { get; }

    public uint ViewCount { get; }
}

/// <summary>
/// Explicit RenderGraph capabilities produced by Cluster geometry. Independent forward,
/// deferred, temporal, lighting, debug, or presentation features may consume any subset.
/// </summary>
public readonly record struct ClusterPipelineOutputs(
    GraphTextureId Visibility,
    GraphTextureId Depth,
    GraphTextureId MaterialColor,
    GraphTextureId MotionVectors,
    ClusterGeometryCapabilities Geometry,
    int Width,
    int Height,
    uint ViewCount)
{
    public bool IsValid =>
        Visibility != default &&
        Depth != default &&
        MaterialColor != default &&
        MotionVectors != default &&
        Geometry is { IsValid: true } &&
        Width > 0 &&
        Height > 0 &&
        ViewCount > 0;
}

/// <summary>
/// Feature-owned policy data for views participating in the shared Cluster work stream.
/// The context contributes only destination and occlusion policy resources; it never owns a
/// second traversal, cull, bin, tessellation, or raster pipeline.
/// </summary>
public readonly record struct ClusterRenderContext(
    uint PolicyId,
    GraphResourceBinder FilterResourcesBinder,
    GraphResourceBinder InvocationViewsBinder,
    ClusterInvocationSourceBinder InvocationSourceBinder,
    GraphResourceBinder InvocationCountBinder,
    GraphBufferId InvocationDispatchArguments,
    GraphBufferId PolicyRasterWorkCounter,
    uint MaximumInvocationCount,
    uint RasterWidth,
    uint RasterHeight,
    GraphPassId ActivationReady,
    GraphPassId IntermediateOcclusionBuildBegin,
    GraphPassId IntermediateOcclusionReady,
    GraphPassId FinalOcclusionBuildBegin,
    GraphPassId FinalOcclusionReady,
    uint OcclusionMipCount,
    uint OcclusionTexelSize,
    GraphPassId FinishBefore = default,
    bool HardwareRasterOnly = false,
    GraphPassId BoundsConsumer = default)
{
    public const uint TargetViewSizeInBytes = 80;

    public bool IsValid =>
        PolicyId != 0u &&
        FilterResourcesBinder is not null &&
        InvocationViewsBinder is not null &&
        InvocationSourceBinder is not null &&
        InvocationCountBinder is not null &&
        InvocationDispatchArguments != default &&
        PolicyRasterWorkCounter != default &&
        MaximumInvocationCount != 0 &&
        RasterWidth != 0 &&
        RasterHeight != 0 &&
        ActivationReady != default &&
        IntermediateOcclusionBuildBegin != default &&
        IntermediateOcclusionReady != default &&
        FinalOcclusionBuildBegin != default &&
        FinalOcclusionReady != default &&
        OcclusionMipCount != 0u &&
        OcclusionTexelSize != 0u;
}

/// <summary>
/// Feature-neutral geometry resources produced by Cluster. Motion, debug, RT export, streamout,
/// and other independent features consume this capability without changing Cluster shader roles.
/// </summary>
public sealed class ClusterGeometryCapabilities
{
    private BufferRange[] _lastResidentPageRanges = [];
    private BufferRangeSet _residentPageReads = BufferRangeSet.Empty;
    private BufferRangeSet _lastResidentPageReads = BufferRangeSet.Empty;
    private BufferRange[] _lastBvhRanges = [];
    private BufferRangeSet _bvhReads = BufferRangeSet.Empty;
    private BufferRangeSet _lastBvhReads = BufferRangeSet.Empty;

    internal ClusterGeometryCapabilities(
        GraphBufferId packedViews,
        GraphBufferId instanceData,
        GraphBufferId instanceProperties,
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
    public uint PackedViewStride => ClusterViewUniforms.SizeInBytes;
    public GraphBufferId InstanceData { get; }
    public GraphBufferId InstanceWorldBounds { get; internal init; }
    public GraphPassId BoundsReady { get; internal init; }
    public GraphBufferId InstanceProperties { get; }
    public BufferRange InstancePropertiesRange { get; }
    public GraphBufferId VisibleClusters { get; }
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

    public void ReadPageHeap(ref PassDefinition pass, PipelineSync sync) =>
        pass.ReadRanges(PageHeap, _residentPageReads, sync, ResourceAccess.ShaderResource);

    public bool HasBvhData => _bvhReads.Count != 0;

    internal BufferRangeSet ResidentPageReads => _residentPageReads;

    internal BufferRangeSet BvhReads => _bvhReads;

    internal void SetFrameRanges(BufferRange[] pageRanges, BufferRange[] bvhRanges)
    {
        ArgumentNullException.ThrowIfNull(pageRanges);
        if (!ReferenceEquals(pageRanges, _lastResidentPageRanges))
        {
            _lastResidentPageRanges = pageRanges;
            _lastResidentPageReads = new BufferRangeSet(pageRanges);
        }
        _residentPageReads = _lastResidentPageReads;
        if (!ReferenceEquals(bvhRanges, _lastBvhRanges))
        {
            _lastBvhRanges = bvhRanges;
            _lastBvhReads = new BufferRangeSet(bvhRanges);
        }
        _bvhReads = _lastBvhReads;
    }

    internal void ClearFrame()
    {
        _residentPageReads = BufferRangeSet.Empty;
        _bvhReads = BufferRangeSet.Empty;
    }
}
