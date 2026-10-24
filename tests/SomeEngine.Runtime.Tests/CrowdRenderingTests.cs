using DefaultGame;
using DefaultGame.Content;
using SomeEngine.Render.Cluster.Pipeline;
using System.Text.Json;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task DefaultGame_crowd_shares_96_bone_actions_and_matches_both_material_paths()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.SetStaticShaderAsync();
        string engine = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(engine, "SomeEngine.slnx"))) engine = Path.GetDirectoryName(engine)!;
        await CrowdContent.BuildAsync(content.Project.RootPath, engine);
        var example = new CrowdExample();
        SceneImage? inline = null;
        ClusterFrameMetrics inlineGeometry = default;
        foreach (string path in new[] { "CrowdInline", "CrowdCache" })
        {
            using var crowd = new CrowdScene(example);
            var frames = new List<ApplicationFrameTiming>();
            SceneImage? image = null;
            await SceneImages.RunAsync("d3d12", new SceneScript(
                crowd.CreateApplication(Path.Combine(content.Project.RootPath, path), frames.Add), 64,
                [new(63, edit => edit.CaptureImage(value => image = value))]),
                "--hardware-adapter", "--no-taa", "--no-vsm", "--fixed-step", "0.016666667");
            Assert.Equal(1000, crowd.Targets.Count);
            Assert.Equal(4, crowd.Poses.Count);
            Assert.Equal(4, crowd.Poses.Select(sample => sample.Pose.Playback.State).Distinct().Count());
            Assert.Equal(4, crowd.Poses.Select(sample => sample.Pose.SampleTime).Distinct().Count());
            Assert.Contains(crowd.Poses, sample => sample.Pose.Playback.TransitionWeight > 0 && sample.Pose.Playback.TransitionWeight < 1);
            Assert.All(crowd.Poses, sample =>
            {
                Assert.Equal(96, sample.Pose.Current.Length);
                Assert.NotEqual(sample.Pose.Current.Span[95], sample.Pose.Previous.Span[95]);
            });
            Assert.Equal(64, frames.Count);
            Assert.All(frames, frame => Assert.Equal(frame.Gpu.FrameIdentity, frame.Geometry.FrameIdentity));
            Assert.True(frames[^1].Geometry.SelectedVertices > 0);
            Assert.True(frames[^1].Geometry.SelectedTriangles > 0);
            _output.WriteLine($"{path} completed geometry: {JsonSerializer.Serialize(frames[^1].Geometry)}");
            SceneImage captured = Assert.IsType<SceneImage>(image);
            Assert.Equal(1920, captured.Width);
            Assert.Equal(1080, captured.Height);
            if (inline is null) { inline = captured; inlineGeometry = frames[^1].Geometry; }
            else
            {
                Assert.Equal(inlineGeometry.SelectedVertices, frames[^1].Geometry.SelectedVertices);
                Assert.Equal(inlineGeometry.SelectedTriangles, frames[^1].Geometry.SelectedTriangles);
                SceneImages.AssertEqualWithinPixel(inline, captured, tolerance: 2);
            }
        }
        int visible = 0;
        for (int pixel = 0; pixel < inline!.Pixels.Length; pixel += 4)
            if (inline.Pixels.Span[pixel] > 32) visible++;
        Assert.True(visible > 5000);
    }
}
