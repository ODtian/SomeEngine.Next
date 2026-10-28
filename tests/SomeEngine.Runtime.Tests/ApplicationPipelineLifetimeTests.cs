using System.Numerics;
using ImGuiNET;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class ApplicationPipelineLifetimeTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ReloadPreservesOutputAfterResizingAndRemovingAViewAcrossShaderCollection(string backend)
    {
        var images = new List<SceneImage>();
        var completed = new List<ApplicationResult>();
        ApplicationView? view = null;
        var application = new ApplicationScene(content.Spheres)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--graphics-backend", backend, "--hidden-window",
                "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa",
                "--no-vsm", "--frames", "4"]),
        };
        application.Initialize = _ => view = application.AddView(new(8, 16, 16));
        application.DrawOverlay = () =>
        {
            if (view is { } image)
                ImGui.GetBackgroundDrawList().AddImage(image.ImageId, Vector2.Zero, new Vector2(32, 24));
        };
        application.UpdateFrame = frame =>
        {
            if (frame.Index == 1) view!.Settings = new(8, 32, 24);
            if (frame.Index == 2)
            {
                Assert.True(application.RemoveView(view!));
                view = null;
            }
            if (frame.Index == 3) frame.Scene.CaptureImage(images.Add);
        };
        application.SceneCompleted = result =>
        {
            completed.Add(result);
            if (completed.Count == 1)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                application.Reload();
            }
        };

        ApplicationResult result = await Application.RunAsync([], application: application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(8, result.CompletedFrames);
        Assert.Equal(2, completed.Count);
        Assert.All(completed, run => Assert.True(run.Succeeded, run.Error?.ToString()));
        Assert.Equal(2, images.Count);
        SceneImages.AssertEqual(images[0], images[1]);
    }
}
