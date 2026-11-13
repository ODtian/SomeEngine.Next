using SomeEngine.Assets.Importers;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationShaderTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SeparateApplicationsShareTheSameEngineShader(string backend)
    {
        Shader? first = null, second = null;
        ApplicationOptions options = ApplicationOptions.Parse([
            "--graphics-backend", backend, "--hidden-window", "--synchronous-frames", "--static-scene",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "2"]);
        var firstScene = new ApplicationScene(content.DirectoryPath)
        {
            Options = options,
            CreateFrameFeatures = rendering =>
            {
                first = rendering.ShaderLibrary.GetAsync(rendering.Files, EngineShaderAssets.GpuPose)
                    .AsTask().GetAwaiter().GetResult();
                return [];
            },
        };

        ApplicationResult firstRun = await Application.RunAsync(["--hardware-adapter"], firstScene);

        Assert.True(firstRun.Succeeded, firstRun.Error?.ToString());
        Assert.Equal(2, firstRun.CompletedFrames);
        var secondScene = new ApplicationScene(content.DirectoryPath)
        {
            Options = options,
            CreateFrameFeatures = rendering =>
            {
                second = rendering.ShaderLibrary.GetAsync(rendering.Files, EngineShaderAssets.GpuPose)
                    .AsTask().GetAwaiter().GetResult();
                return [];
            },
        };

        ApplicationResult secondRun = await Application.RunAsync(["--hardware-adapter"], secondScene);

        Assert.True(secondRun.Succeeded, secondRun.Error?.ToString());
        Assert.Equal(2, secondRun.CompletedFrames);
        Assert.NotNull(first);
        Assert.Same(first, second);
    }
}
