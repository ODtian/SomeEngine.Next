using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Fixed geometry-output shape for one independently instantiable Cluster pipeline. It contains
/// no swapchain, presentation, lighting, temporal, or post-processing policy.
/// </summary>
public readonly record struct ClusterPipelineOutputDescription
{
    public ClusterPipelineOutputDescription(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>
/// Explicit RenderGraph capabilities produced by Cluster geometry. Independent forward,
/// deferred, temporal, lighting, debug, or presentation features may consume any subset.
/// </summary>
public readonly record struct ClusterPipelineOutputs(
    GraphTextureId Visibility,
    GraphTextureId Depth,
    GraphTextureId MaterialColor,
    int Width,
    int Height)
{
    public bool IsValid =>
        Visibility != default &&
        Depth != default &&
        MaterialColor != default &&
        Width > 0 &&
        Height > 0;
}
