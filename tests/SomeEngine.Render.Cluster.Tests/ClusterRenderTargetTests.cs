using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterRenderTargetTests
{
    [Fact]
    public void Mailbox_derives_metadata_and_allows_each_published_target_to_be_taken_once()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterInfo adapter = SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                32,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));

        var target = new ClusterRenderTarget(texture);
        Assert.Same(texture, target.Texture);
        Assert.Equal(64, target.Width);
        Assert.Equal(32, target.Height);
        Assert.Equal(Format.R8G8B8A8UNorm, target.Format);

        var mailbox = new ClusterRenderTargetMailbox();
        mailbox.Publish(target);
        Assert.Throws<InvalidOperationException>(() => mailbox.Publish(target));
        Assert.Equal(target, mailbox.TakeRequired());
        Assert.Throws<InvalidOperationException>(() => mailbox.TakeRequired());
    }

    [Fact]
    public void Render_target_rejects_a_texture_that_cannot_be_a_color_attachment()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterInfo adapter = SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));

        Assert.Throws<ArgumentException>(() => new ClusterRenderTarget(texture));
    }

    private static AdapterInfo SelectWarp(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        if (count == 0)
            throw new NotSupportedException("No Direct3D 12 adapter is available.");

        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }

        return adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated) is
        { Name.Length: > 0 } warp
                ? warp
                : throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
    }
}
