using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>The normally acquired presentation image consumed by the Cluster frame system.</summary>
public readonly record struct ClusterRenderTarget(
    Texture Texture,
    int Width,
    int Height,
    Format Format)
{
    public bool IsValid =>
        Texture is not null && Width > 0 && Height > 0 && Enum.IsDefined(Format);
}

/// <summary>
/// One-frame presentation capability shared by the application host and render-frame systems.
/// It contains no asset identity and creates no alternate startup path: the normal host publishes
/// the image returned by its swapchain immediately before running the ordinary frame group.
/// </summary>
public sealed class ClusterRenderTargetSource
{
    private readonly object _gate = new();
    private ClusterRenderTarget _current;

    public void Publish(in ClusterRenderTarget target)
    {
        if (!target.IsValid)
            throw new ArgumentException("A Cluster render target must be complete.", nameof(target));
        lock (_gate)
            _current = target;
    }

    internal ClusterRenderTarget GetRequired()
    {
        lock (_gate)
        {
            return _current.IsValid
                ? _current
                : throw new InvalidOperationException(
                    "The application host did not publish its acquired render target.");
        }
    }
}
