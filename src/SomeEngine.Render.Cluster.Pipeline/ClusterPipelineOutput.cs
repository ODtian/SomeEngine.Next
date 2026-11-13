using SomeEngine.Render;
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
        if (viewCount > RenderRasterContext.MaximumViewCount)
            throw new ArgumentOutOfRangeException(nameof(viewCount), "Primary view identities exceed the Cluster draw ABI.");
        if (checked((ulong)width * (ulong)height * viewCount) > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(viewCount), "The combined view pixels must fit the 32-bit shading counters and offsets.");
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
public readonly record struct ClusterPipelineOutputs(GraphTextureId Visibility, GraphTextureId Depth, GraphTextureId MaterialColor, GraphTextureId MotionVectors, RenderGeometryResources Geometry, int Width, int Height, uint ViewCount)
{
    public GraphTextureId CurrentHiZ { get; init; }
    public bool IsValid => Visibility != default && Depth != default && MaterialColor != default && MotionVectors != default && Geometry is { IsValid: true } && Width > 0 && Height > 0 && ViewCount > 0;
}
