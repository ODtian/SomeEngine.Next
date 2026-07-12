using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class SwapchainTests
{
    [Fact]
    public void Null_enforces_acquire_present_resize_and_backbuffer_lifetime()
    {
        using var device = new Device();
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            0,
            4,
            4,
            Format.R8G8B8A8UNorm,
            BufferCount: 2));

        SwapchainImage first = device.AcquireNextImage(swapchain);
        Assert.Equal(4, device.GetTextureMetadata(first.Texture).Description.Width);
        Assert.Throws<InvalidOperationException>(() => device.AcquireNextImage(swapchain));
        Assert.Throws<InvalidOperationException>(() => device.DestroyTexture(first.Texture));
        Assert.True(device.Present(swapchain, first.ImageIndex).Succeeded);
        Assert.Throws<InvalidOperationException>(() => device.Present(swapchain, first.ImageIndex));

        device.Resize(swapchain, 8, 6);
        SwapchainImage resized = device.AcquireNextImage(swapchain);
        TextureDesc resizedDesc = device.GetTextureMetadata(resized.Texture).Description;
        Assert.Equal(8, resizedDesc.Width);
        Assert.Equal(6, resizedDesc.Height);
        Assert.Throws<InvalidOperationException>(() => device.Resize(swapchain, 16, 16));
        Assert.True(device.Present(swapchain, resized.ImageIndex).Succeeded);

        device.DestroySwapchain(swapchain);
        device.CollectGarbage();
        Assert.ThrowsAny<ArgumentException>(() => device.AcquireNextImage(swapchain));
        Assert.ThrowsAny<ArgumentException>(() => device.GetTextureMetadata(resized.Texture));
    }
}
