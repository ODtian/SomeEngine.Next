using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeStartupOptionsConsumerTests
{
    [Fact]
    public void SpatialInstanceGroupsAreAnExplicitCullingChoice()
    {
        Assert.False(ApplicationOptions.Parse([]).UseInstanceGroups);
        Assert.True(ApplicationOptions.Parse(["--instance-groups"]).UseInstanceGroups);
    }

    [Fact]
    public void ImageExportAndFeatureComparisonsKeepVerificationOptional()
    {
        var options = ApplicationOptions.Parse(["--frame-output", "scene.bmp", "--no-debug-ui", "--no-taa", "--no-vsm"]);
        Assert.Equal("scene.bmp", options.FrameOutputPath);
        Assert.True(options.HideDebugUi);
        Assert.False(options.VerifyFrameOutput);
        Assert.False(options.TemporalAntialiasing);
        Assert.False(options.VirtualShadows);
        Assert.True(ApplicationOptions.Parse([]).TemporalAntialiasing);
        Assert.True(ApplicationOptions.Parse([]).VirtualShadows);
        Assert.Throws<ArgumentException>(() => ApplicationOptions.Parse(["--frame-output", "scene.png"]));
    }

    [Fact]
    public void TracePlaybackControlsKeepCaptureAndSceneBehaviorSeparate()
    {
        var options = ApplicationOptions.Parse([
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
        => Assert.Throws<ArgumentException>(() => ApplicationOptions.Parse(["--fixed-step", seconds]));

    [Theory]
    [InlineData("motion")]
    [InlineData("materials")]
    [InlineData("camera")]
    public void ScriptedSceneChangesCanUseNormalFrameOverlap(string playback)
    {
        ApplicationOptions options = ApplicationOptions.Parse(["--scene-playback", playback]);
        Assert.Equal(playback, options.ScenePlayback);
        Assert.False(options.SynchronousFrames);
    }

    [Fact]
    public void Hidden_validation_can_force_the_hardware_path_without_changing_the_adapter()
    {
        ApplicationOptions options = ApplicationOptions.Parse(["--hidden-window", "--hardware-raster"]);
        Assert.True(options.HideWindow);
        Assert.True(options.ForceHardwareRaster);
        Assert.Equal(ApplicationGraphicsBackend.Direct3D12, options.GraphicsBackend);
        Assert.False(ApplicationOptions.Parse([]).HideWindow);
        Assert.False(ApplicationOptions.Parse([]).ForceHardwareRaster);
    }

    [Fact]
    public void ParseExposesHeadlessFrameAndPipelineControls()
    {
        var options = ApplicationOptions.Parse([
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
        ApplicationOptions options = ApplicationOptions.Parse([option, value]);

        Assert.Equal(ApplicationGraphicsBackend.Vulkan, options.GraphicsBackend);
    }

    [Fact]
    public void ParseUsesD3D12ByDefaultAndRejectsUnknownBackends()
    {
        Assert.Equal(
            ApplicationGraphicsBackend.Direct3D12,
            ApplicationOptions.Parse([]).GraphicsBackend);
        Assert.Throws<ArgumentException>(() =>
            ApplicationOptions.Parse(["--graphics-backend", "metal"]));
    }
}
