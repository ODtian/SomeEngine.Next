using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render;

public delegate void RenderRasterSourceBinder(ref RenderGraphFrame graph, GraphBufferSlice sourceViews);

/// <summary>
/// Feature-owned policy data for views participating in the shared Cluster work stream.
/// The context contributes only destination and occlusion policy resources; it never owns a
/// second traversal, cull, bin, tessellation, or raster pipeline.
/// Root dispatch arguments use ceil(instance count / 64) X groups and
/// <see cref="GetViewWorkGroupCount"/> view groups split across Y and Z at
/// 65535 groups per Y dimension. GPU producers use
/// ClusterViewWorkGroups from cluster_view_work.slang for the same dimensions.
/// </summary>
public readonly record struct RenderRasterContext(
    uint PolicyId,
    GraphResourceBinder DispatchViewsBinder,
    RenderRasterSourceBinder DispatchSourceBinder,
    GraphResourceBinder DispatchCountBinder,
    GraphBufferId DispatchArguments,
    GraphBufferId PolicyRasterWorkCounter,
    uint MaximumDispatchCount,
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
    /// <summary>Element in the aligned uint view supplied by DispatchCountBinder; defaults to a scalar count.</summary>
    public uint DispatchCountIndex { get; init; }
    /// <summary>Maximum number of raster windows across all details of one source view.</summary>
    public uint MaximumRasterWindowsPerSourceView { get; init; }

    public bool IsValid =>
        PolicyId != 0u &&
        DispatchViewsBinder is not null &&
        DispatchSourceBinder is not null &&
        DispatchCountBinder is not null &&
        DispatchArguments != default &&
        PolicyRasterWorkCounter != default &&
        MaximumDispatchCount is > 0 and <= MaximumViewCount &&
        RasterWidth != 0 &&
        RasterHeight != 0 &&
        OcclusionMipCount != 0u &&
        OcclusionTexelSize != 0u;
}

