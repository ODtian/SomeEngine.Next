using System.Numerics;
using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class FrameOutputTests
{
    [Fact]
    public void SelectedLinearOutputIsAcceptedWithoutChangingDefaultRendering()
    {
        RuntimeStartupOptions ordinary = RuntimeStartupOptions.Parse(["--frames", "64"]);
        RuntimeStartupOptions capture = RuntimeStartupOptions.Parse(["--frames", "64",
            "--linear-frame-output", "visibility.rgba16", "--capture-frame", "63"]);

        Assert.NotEqual(ordinary, capture);
        Assert.Equal("visibility.rgba16", capture.LinearFrameOutputPath);
        Assert.Equal(63, capture.CaptureFrame);
        Assert.True(capture.TemporalAntialiasing && capture.AsyncCompute && capture.DynamicScene);
        Assert.False(capture.SynchronousFrames || capture.ForceHardwareRaster || capture.DisableMeshShaders);
    }

    [Fact]
    public void LinearOutputRetainsHdrColorAndMatchingSubmittedFrameInputs()
    {
        string path = Path.Combine(Path.GetTempPath(), $"linear-color-{Guid.NewGuid():N}.rgba16");
        var frame = new SceneFrame
        {
            Frame = 63, SubmittedFrame = 62, Width = 2, Height = 1, SceneSeconds = 1.05f,
            View = Matrix4x4.CreateTranslation(0, 0, 3),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 2, .1f, 100),
            JitterPixels = new(-.4375f, .3888889f), CameraPosition = new(0, 0, -3),
            NearPlane = .1f, FarPlane = 100,
            Meshes = [new(Guid.NewGuid(), Matrix4x4.CreateTranslation(1, 2, 3))],
        };
        Half[] pixels = [(Half)4, (Half).33325f, (Half)0, (Half)1, (Half)0, (Half)0, (Half)0, (Half)0];
        try
        {
            new SceneLinearImage(frame, pixels).Save(path);
            Assert.Equal(new byte[] { 0x00, 0x44, 0x55, 0x35, 0x00, 0x00, 0x00, 0x3c,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, File.ReadAllBytes(path));
            SceneLinearImage retained = SceneLinearImage.Load(path);
            Assert.Equal(pixels, retained.Pixels.ToArray());
            Assert.Equal(frame.Frame, retained.Frame.Frame);
            Assert.Equal(frame.SubmittedFrame, retained.Frame.SubmittedFrame);
            Assert.Equal(frame.SceneSeconds, retained.Frame.SceneSeconds);
            Assert.Equal(frame.View, retained.Frame.View);
            Assert.Equal(frame.Projection, retained.Frame.Projection);
            Assert.Equal(frame.CameraPosition, retained.Frame.CameraPosition);
            Assert.Equal(frame.JitterPixels, retained.Frame.JitterPixels);
            Assert.Equal(frame.Meshes, retained.Frame.Meshes);
            Assert.Equal(frame.TemporalSettings, retained.Frame.TemporalSettings);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".json");
        }
    }

    [Fact]
    public void FiniteBeautyAndLinearOutputSelectOnlyTheLastFrameByDefault()
    {
        var options = RuntimeStartupOptions.Parse(["--frames", "64", "--frame-output", "beauty.bmp",
            "--linear-frame-output", "color.rgba16"]);
        Assert.Equal(63, options.CaptureFrame);
        Assert.False(options.VerifyFrameOutput || options.SynchronousFrames);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("64")]
    public void CaptureOutsideFiniteRunIsRejected(string frame)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeStartupOptions.Parse([
            "--frames", "64", "--linear-frame-output", "color.rgba16", "--capture-frame", frame]));
        Assert.Equal("--capture-frame", error.ParamName);
    }
}
