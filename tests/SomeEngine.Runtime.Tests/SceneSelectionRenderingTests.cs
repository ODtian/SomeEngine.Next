using DefaultGame;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task Scene_platform_measures_the_selected_frame_range_and_simulation_clock()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.SetStaticShaderAsync();
        string[] options = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window",
            "--frames", "36", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm",
            "--fixed-step", "0.016666667"];
        var platform = new SceneTestPlatform([new("Fixed frame sample", content.Project.RootPath) { MeasurementFrames = new(32, 4) }], RuntimeStartupOptions.Parse(options))
        {
            ExitAfterMeasurements = true,
        };
        platform.StartMeasurements();
        Assert.Equal(0, await Application.RunAsync(options, animateMaterial: null, platform.Application));
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(platform.LastReport!));
        Assert.True(report.RootElement.GetProperty("Complete").GetBoolean());
        var costs = report.RootElement.GetProperty("Scenes")[0].GetProperty("Costs");
        Assert.True(costs.GetProperty("AsynchronousGameUpdates").GetBoolean());
        Assert.Equal(4, costs.GetProperty("Frames").GetInt32());
        Assert.Equal(32, costs.GetProperty("FrameRange").GetProperty("First").GetInt32());
        Assert.Equal(35, costs.GetProperty("FrameRange").GetProperty("Last").GetInt32());
        Assert.Equal(32d / 60, costs.GetProperty("SimulationSeconds").GetProperty("First").GetDouble(), precision: 6);
        Assert.Equal(35d / 60, costs.GetProperty("SimulationSeconds").GetProperty("Last").GetDouble(), precision: 6);
        File.Delete(platform.LastReport!);
        File.Delete(Path.Combine(Path.GetDirectoryName(platform.LastReport!)!, "frame-samples.json"));
        Directory.Delete(Path.GetDirectoryName(platform.LastReport!)!);
    }

    [Fact]
    public async Task Selected_scene_runs_its_game_callbacks_and_complete_frame_measurements()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.SetStaticShaderAsync();
        int starts = 0, updates = 0;
        var measured = new List<ApplicationFrameTiming>();
        string[] options = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window",
            "--frames", "4", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm"];
        var selected = new TestScene("Game callbacks", content.Project.RootPath)
        {
            Initialize = _ => starts++,
            UpdateFrame = _ => updates++,
            FrameMeasured = measured.Add,
        };
        var platform = new SceneTestPlatform([selected], RuntimeStartupOptions.Parse(options));
        SceneImage? image = null;
        Action<ApplicationFrame> completed = platform.Application.FrameCompleted!;
        platform.Application.FrameCompleted = frame =>
        {
            completed(frame);
            if (frame.Index == 0) platform.Application.CaptureImage(value => image = value);
        };
        int result = await Application.RunAsync(options, animateMaterial: null, platform.Application);
        Assert.Equal(1, starts);
        Assert.Equal(0, result);
        Assert.Equal(4, updates);
        Assert.Equal(4, measured.Count);
        Assert.All(measured, frame => Assert.True(frame.Gpu.ElapsedMs > 0));
        Assert.Equal(1, Assert.IsType<SceneImage>(image).Frame);
    }
}
