using System.Numerics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ProjectionMetadataTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12", false)]
    [InlineData("d3d12", true)]
    public async Task LinearCapturesKeepUnjitteredProjectionSeparateFromTemporalJitter(
        string backend, bool temporalAntialiasing)
    {
        var images = new List<SceneLinearImage>();
        var script = new SceneScript(content.DirectoryPath, 5,
        [
            new(3, edit => edit.CaptureLinearImage(images.Add)),
            new(4, edit => edit.CaptureLinearImage(images.Add)),
        ]);
        await SceneImages.RunAsync(backend, script,
            temporalAntialiasing ? "--taa" : "--no-taa", "--no-vsm");

        Assert.Equal(2, images.Count);
        Assert.Equal(3, images[0].Frame.Frame);
        Assert.Equal(4, images[1].Frame.Frame);
        SceneFrame firstFrame = images[0].Frame;
        Assert.True(firstFrame.Width > 0);
        Assert.True(firstFrame.Height > 0);
        Matrix4x4 unjittered = firstFrame.UnjitteredProjection;
        foreach (SceneLinearImage image in images)
        {
            SceneFrame frame = image.Frame;
            Assert.Equal(firstFrame.Width, frame.Width);
            Assert.Equal(firstFrame.Height, frame.Height);
            Assert.Equal(unjittered, frame.UnjitteredProjection);
            Assert.InRange(MathF.Abs(frame.UnjitteredProjection.M22 / frame.UnjitteredProjection.M11 -
                (float)frame.Width / frame.Height), 0f, 0.00001f);
            if (!temporalAntialiasing) Assert.Equal(Vector2.Zero, frame.JitterPixels);
            if (frame.JitterPixels == Vector2.Zero)
                Assert.Equal(frame.UnjitteredProjection, frame.Projection);
            else
                Assert.NotEqual(frame.UnjitteredProjection, frame.Projection);
        }
        if (temporalAntialiasing)
            Assert.Contains(images, image => image.Frame.JitterPixels != Vector2.Zero);
    }
}
