namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
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
            CreateApplication = path => new(path)
            {
                Initialize = _ => starts++,
                UpdateFrame = _ => updates++,
                FrameMeasured = measured.Add,
            },
        };
        var platform = new SceneTestPlatform([selected], RuntimeStartupOptions.Parse(options));
        int result = await Application.RunAsync(options, animateMaterial: null, platform);
        Assert.Equal(1, starts);
        Assert.Equal(0, result);
        Assert.Equal(4, updates);
        Assert.Equal(4, measured.Count);
        Assert.All(measured, frame => Assert.True(frame.Gpu.ElapsedMs > 0));
    }
}
