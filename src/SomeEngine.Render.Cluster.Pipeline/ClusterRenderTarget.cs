using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>A borrowed presentation image with dimensions derived from its RHI metadata.</summary>
public readonly record struct ClusterRenderTarget
{
    public ClusterRenderTarget(Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        TextureInfo info = texture.Info;
        if (info.Dimension != TextureDimension.Texture2D ||
            info.Width == 0 ||
            info.Height == 0 ||
            info.Depth != 1 ||
            info.ArrayLayerCount != 1 ||
            info.SampleCount != 1 ||
            (info.Usages & TextureUsages.ColorAttachment) == 0)
        {
            throw new ArgumentException(
                "A Cluster render target must be a non-empty single-sample 2D color attachment.",
                nameof(texture));
        }

        Texture = texture;
        Width = checked((int)info.Width);
        Height = checked((int)info.Height);
        Format = info.Format;
    }

    public Texture Texture { get; }

    public int Width { get; }

    public int Height { get; }

    public Format Format { get; }

    public bool IsValid => Texture is not null;
}

/// <summary>
/// One-frame presentation capability shared by the application host and render-frame systems.
/// It contains no asset identity and creates no alternate startup path: the normal host publishes
/// the image returned by its swapchain immediately before running the ordinary frame group.
/// </summary>
public sealed class ClusterRenderTargetMailbox
{
    private readonly object _gate = new();
    private ClusterRenderTarget _pending;
    private bool _hasPending;

    public void Publish(in ClusterRenderTarget target)
    {
        if (!target.IsValid)
            throw new ArgumentException("A Cluster render target must be complete.", nameof(target));
        lock (_gate)
        {
            if (_hasPending)
            {
                throw new InvalidOperationException(
                    "The previous Cluster render target has not been consumed.");
            }
            _pending = target;
            _hasPending = true;
        }
    }

    internal ClusterRenderTarget TakeRequired()
    {
        lock (_gate)
        {
            if (!_hasPending)
            {
                throw new InvalidOperationException(
                    "The application host did not publish an acquired render target for this frame.");
            }
            ClusterRenderTarget result = _pending;
            _pending = default;
            _hasPending = false;
            return result;
        }
    }
}
