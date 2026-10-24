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
        // DrawRequest has a 20-bit view identity, with one bit selecting target views.
        if (viewCount > ClusterRenderContext.MaximumViewCount)
            throw new ArgumentOutOfRangeException(nameof(viewCount), "Primary view identities exceed the Cluster draw ABI.");
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
/// Forward and deferred shading expose the same color, depth, and motion textures to post-processing.
/// </summary>
/// <remarks>
/// Depth is native D32Float visibility depth in [0, 1], cleared to 1 for background.
/// MaterialColor is linear R16G16B16A16Float before temporal resolve and tone mapping.
/// MotionVectors is R16G16Float current projected UV minus previous projected UV, per view.
/// </remarks>
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
    public GraphTextureId CurrentHiZ { get; init; }
    public ClusterTransparencyGeometry? Transparency { get; init; }
    public ClusterStencil? Stencil { get; init; }
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

/// <summary>Projected transparent geometry, source data and continuous view-cell references.</summary>
/// <remarks>TriangleCount stores the actual projected triangle count at byte 0.
/// Cell counts and offsets address complete continuous ranges. Streaming cells enumerate the paired
/// source geometry in batches. Triangle records retain screen projection, depth, view, material bin,
/// source identity and primitive number. TriangleCapacity specifies the resident projection budget.</remarks>
public sealed record ClusterTransparencyGeometry(GraphBufferId Triangles, GraphBufferId TileCounts,
    GraphBufferId TileReferences, GraphBufferId TriangleCount, uint TriangleCapacity, uint TriangleStride,
    GraphPassId GeometryReady)
{
    public GraphBufferId CellOffsets { get; init; }
    public GraphBufferId StreamingCells { get; init; }
    public GraphBufferId Sources { get; init; }
    public GraphBufferId SourceCounts { get; init; }
    public uint ReferenceCapacity { get; init; }
}

/// <summary>
/// Feature-owned policy data for views participating in the shared Cluster work stream.
/// The context contributes only destination and occlusion policy resources; it never owns a
/// second traversal, cull, bin, tessellation, or raster pipeline.
/// Root dispatch arguments use ceil(instance count / 64) X groups and
/// <see cref="GetViewWorkGroupCount"/> view groups split across Y and Z at
/// 65535 groups per Y dimension. GPU producers use
/// ClusterViewWorkGroups from cluster_view_work.slang for the same dimensions.
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
    uint OcclusionMipCount,
    uint OcclusionTexelSize,
    bool HardwareRasterOnly = false)
{
    public const uint MaximumViewCount = 1u << 19;
    public const uint TargetViewSizeInBytes = 96;
    public const int ViewSizeInBytes = 816;

    /// <summary>Distributes each compressed 32-view batch across instance and view threads.</summary>
    public static uint GetViewWorkGroupCount(uint viewCount)
    {
        uint tail = viewCount & 31u;
        return (viewCount >> 5) * 16u + (tail == 0u ? 0u :
            System.Numerics.BitOperations.RoundUpToPowerOf2(Math.Min(tail, 16u)));
    }

    public GraphResourceBinder? BindTargetPageBounds { get; init; }
    public GraphResourceBinder? BindWorkTargetViews { get; init; }
    public GraphResourceBinder? BindDepthCacheClasses { get; init; }
    public GraphBufferId DepthWorkCounter { get; init; }
    public uint ViewDetailCount { get; init; } = 1u;
    /// <summary>Element in the aligned uint view supplied by InvocationCountBinder; defaults to a scalar count.</summary>
    public uint InvocationCountIndex { get; init; }
    /// <summary>Maximum number of raster windows across all details of one source view.</summary>
    public uint MaximumRasterWindowsPerSourceView { get; init; }

    public bool IsValid =>
        PolicyId != 0u &&
        FilterResourcesBinder is not null &&
        InvocationViewsBinder is not null &&
        InvocationSourceBinder is not null &&
        InvocationCountBinder is not null &&
        InvocationDispatchArguments != default &&
        PolicyRasterWorkCounter != default &&
        MaximumInvocationCount is > 0 and <= MaximumViewCount &&
        RasterWidth != 0 &&
        RasterHeight != 0 &&
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
    private BufferRangeSet _lastBvhReadRanges = BufferRangeSet.Empty;
    private BufferRangeSet _bvhReadRanges = BufferRangeSet.Empty;

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
    /// <summary>Whole-frame ClusterWorkOverflow flags. Nonzero forbids publishing cached depth as complete.</summary>
    public GraphBufferId WorkStatus { get; internal init; }
    public GraphPassId BoundsReady { get; internal init; }
    public GraphBufferId InstanceProperties { get; }
    public BufferRange InstancePropertiesRange { get; }
    public GraphBufferId VisibleClusters { get; }
    public GraphBufferId DeformCache { get; internal init; }
    public GraphBufferId DeformRegions { get; internal init; }
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

    public bool HasBvhData => _bvhReadRanges.Count != 0;

    internal BufferRangeSet ResidentPageReads => _residentPageReads;

    internal BufferRangeSet BvhReadRanges => _bvhReadRanges;

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
            _lastBvhReadRanges = bvhRanges.Length == 0 ? BufferRangeSet.Empty : new BufferRangeSet(bvhRanges);
        }
        _bvhReadRanges = _lastBvhReadRanges;
    }

    internal void ClearFrame()
    {
        _residentPageReads = BufferRangeSet.Empty;
        _bvhReadRanges = BufferRangeSet.Empty;
    }
}
