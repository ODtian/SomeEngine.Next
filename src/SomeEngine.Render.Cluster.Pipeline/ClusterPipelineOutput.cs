using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Fixed geometry-output shape for one independently instantiable Cluster pipeline. It contains
/// no swapchain, presentation, lighting, temporal, or post-processing policy.
/// </summary>
public readonly record struct ClusterPipelineOutputDescription
{
    public ClusterPipelineOutputDescription(int width, int height, uint viewCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(viewCapacity);
        Width = width;
        Height = height;
        ViewCapacity = viewCapacity;
    }

    public int Width { get; }

    public int Height { get; }

    public uint ViewCapacity { get; }
}

/// <summary>
/// Explicit RenderGraph capabilities produced by Cluster geometry. Independent forward,
/// deferred, temporal, lighting, debug, or presentation features may consume any subset.
/// </summary>
public readonly record struct ClusterPipelineOutputs(
    GraphTextureId Visibility,
    GraphTextureId Depth,
    GraphTextureId MaterialColor,
    ClusterGeometryCapabilities Geometry,
    int Width,
    int Height,
    uint ViewCapacity)
{
    public bool IsValid =>
        Visibility != default &&
        Depth != default &&
        MaterialColor != default &&
        Geometry is { IsValid: true } &&
        Width > 0 &&
        Height > 0 &&
        ViewCapacity > 0;
}

/// <summary>
/// Feature-neutral geometry resources produced by Cluster. Motion, debug, RT export, streamout,
/// and other independent features consume this capability without changing Cluster shader roles.
/// </summary>
public sealed class ClusterGeometryCapabilities
{
    private BufferRange[] _residentPageRanges = [];

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
        GraphBufferId deformCache,
        GraphBufferId cacheOffsets)
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
        DeformCache = deformCache;
        CacheOffsets = cacheOffsets;
    }

    public GraphBufferId PackedViews { get; }
    public GraphBufferId InstanceData { get; }
    public GraphBufferId InstanceProperties { get; }
    public BufferRange InstancePropertiesRange { get; }
    public GraphBufferId VisibleClusters { get; }
    public GraphBufferId PhaseOneCounts { get; }
    public GraphBufferId PhaseTwoCounts { get; }
    public uint VisibleClusterCapacity { get; }
    public GraphBufferId PageHeap { get; }
    public GraphBufferId Bvh { get; }
    public GraphBufferId DeformCache { get; }
    public GraphBufferId CacheOffsets { get; }

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
        DeformCache != default &&
        CacheOffsets != default &&
        InstancePropertiesRange.Size != 0;

    /// <summary>
    /// Binds the page heap and declares only the authenticated resident ranges published for this
    /// frame. Consumers cannot accidentally turn descriptor reachability into an RG dependency.
    /// </summary>
    public void BindPageHeap(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        PipelineSync sync,
        string? label = null)
    {
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(PageHeap, label: label ?? "Cluster geometry page heap")));
        foreach (ref readonly BufferRange range in _residentPageRanges.AsSpan())
        {
            _ = pass.Read(
                PageHeap,
                range,
                sync,
                ResourceAccess.ShaderResource);
        }
    }

    internal void SetResidentPageRanges(BufferRange[] ranges) =>
        _residentPageRanges = ranges ?? throw new ArgumentNullException(nameof(ranges));

    internal void ClearFrame() => _residentPageRanges = [];
}
