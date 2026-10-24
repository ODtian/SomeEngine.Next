using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeStartupOptionsConsumerTests
{
    [Fact]
    public void SpatialInstanceGroupsAreAnExplicitCullingChoice()
    {
        Assert.False(RuntimeStartupOptions.Parse([]).UseInstanceGroups);
        Assert.True(RuntimeStartupOptions.Parse(["--instance-groups"]).UseInstanceGroups);
    }

    [Fact]
    public void ImageExportAndFeatureComparisonsKeepVerificationOptional()
    {
        var options = RuntimeStartupOptions.Parse(["--frame-output", "scene.bmp", "--no-debug-ui", "--no-taa", "--no-vsm"]);
        Assert.Equal("scene.bmp", options.FrameOutputPath);
        Assert.True(options.HideDebugUi);
        Assert.False(options.VerifyFrameOutput);
        Assert.False(options.TemporalAntialiasing);
        Assert.False(options.VirtualShadows);
        Assert.True(RuntimeStartupOptions.Parse([]).TemporalAntialiasing);
        Assert.True(RuntimeStartupOptions.Parse([]).VirtualShadows);
        Assert.Throws<ArgumentException>(() => RuntimeStartupOptions.Parse(["--frame-output", "scene.png"]));
    }

    [Fact]
    public void TracePlaybackControlsKeepCaptureAndSceneBehaviorSeparate()
    {
        var options = RuntimeStartupOptions.Parse([
            "--trace", "capture.setrace", "--trace-detail", "--trace-allocations",
            "--synchronous-frames", "--scene-playback", "materials", "--fixed-step", "0.02"]);
        Assert.Equal("capture.setrace", options.TraceOutput);
        Assert.True(options.TraceDetail);
        Assert.True(options.TraceAllocations);
        Assert.True(options.SynchronousFrames);
        Assert.Equal("materials", options.ScenePlayback);
        Assert.Equal(0.02f, options.FixedFrameSeconds);
        Assert.False(options.Profiler.EnableTracy);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void FixedStepsMustBeFiniteAndPositive(string seconds)
        => Assert.Throws<ArgumentException>(() => RuntimeStartupOptions.Parse(["--fixed-step", seconds]));

    [Theory]
    [InlineData("motion")]
    [InlineData("materials")]
    [InlineData("camera")]
    public void ScriptedSceneChangesCanUseNormalFrameOverlap(string playback)
    {
        RuntimeStartupOptions options = RuntimeStartupOptions.Parse(["--scene-playback", playback]);
        Assert.Equal(playback, options.ScenePlayback);
        Assert.False(options.SynchronousFrames);
    }

    [Fact]
    public void Hidden_validation_can_force_the_hardware_path_without_changing_the_adapter()
    {
        RuntimeStartupOptions options = RuntimeStartupOptions.Parse(["--hidden-window", "--hardware-raster"]);
        Assert.True(options.HideWindow);
        Assert.True(options.ForceHardwareRaster);
        Assert.Equal(RuntimeGraphicsBackend.Direct3D12, options.GraphicsBackend);
        Assert.False(RuntimeStartupOptions.Parse([]).HideWindow);
        Assert.False(RuntimeStartupOptions.Parse([]).ForceHardwareRaster);
    }

    [Fact]
    public void ParseExposesHeadlessFrameAndPipelineControls()
    {
        var options = RuntimeStartupOptions.Parse([
            "--frames", "3",
            "--no-vsync",
            "--skip-present"]);

        Assert.Equal(3, options.FrameLimit);
        Assert.False(options.WindowVSync);
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
