using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeStartupOptionsConsumerTests
{
    [Fact]
    public void ParseExposesHeadlessFrameAndPipelineControls()
    {
        var options = RuntimeStartupOptions.Parse([
            "--frames", "3",
            "--no-vsync",
            "--present-interval", "0",
            "--pipeline-budget", "8",
            "--wait-pipelines",
            "--skip-present"]);

        Assert.Equal(3, options.FrameLimit);
        Assert.False(options.WindowVSync);
        Assert.Equal(0u, options.PresentSyncInterval);
        Assert.Equal(8, options.PipelineWarmupBudget);
        Assert.True(options.WaitForPipelineWarmup);
        Assert.True(options.SkipSwapchainPresent);
    }

    [Theory]
    [InlineData("--graphics-backend", "vulkan")]
    [InlineData("--backend", "VULKAN")]
    public void ParseSelectsTheVulkanBackend(string option, string value)
    {
        RuntimeStartupOptions options = RuntimeStartupOptions.Parse([option, value]);

        Assert.Equal(RuntimeGraphicsBackend.Vulkan, options.GraphicsBackend);
    }

    [Fact]
    public void ParseUsesD3D12ByDefaultAndRejectsUnknownBackends()
    {
        Assert.Equal(
            RuntimeGraphicsBackend.Direct3D12,
            RuntimeStartupOptions.Parse([]).GraphicsBackend);
        Assert.Throws<ArgumentException>(() =>
            RuntimeStartupOptions.Parse(["--graphics-backend", "metal"]));
    }
}
