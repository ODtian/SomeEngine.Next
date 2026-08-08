using System.Numerics;
using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeViewFrameTests
{
    [Fact]
    public void TemporalFramesApplyAndWrapTheCenteredHaltonJitter()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            new Vector3(0.0f, 0.0f, 5.0f),
            Vector3.Zero,
            Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3.0f,
            16.0f / 9.0f,
            0.1f,
            1000.0f);

        RuntimeViewFrame first = RuntimeViewFrame.Create(view, projection, 1920, 1080, 0);
        RuntimeViewFrame second = RuntimeViewFrame.Create(view, projection, 1920, 1080, 1);
        RuntimeViewFrame wrapped = RuntimeViewFrame.Create(view, projection, 1920, 1080, 8);

        Assert.Equal(1920u, first.View.ViewportWidth);
        Assert.Equal(1080u, first.View.ViewportHeight);
        Assert.NotEqual(Vector2.Zero, first.JitterPixels);
        Assert.NotEqual(first.JitterPixels, second.JitterPixels);
        Assert.NotEqual(projection, first.View.Projection);
        Assert.NotEqual(first.View.Projection, second.View.Projection);
        Assert.Equal(first.JitterPixels, wrapped.JitterPixels);
        Assert.Equal(first.View.Projection, wrapped.View.Projection);
    }

    [Fact]
    public void TemporalFramesCanPreserveAnUnjitteredProjection()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3.0f,
            1.0f,
            0.1f,
            100.0f);

        RuntimeViewFrame frame = RuntimeViewFrame.Create(
            Matrix4x4.Identity,
            projection,
            640,
            640,
            5,
            temporalResolveEnabled: false);

        Assert.Equal(Vector2.Zero, frame.JitterPixels);
        Assert.Equal(projection, frame.View.Projection);
    }

    [Fact]
    public void CameraCutIsCarriedByTheAuthoredRenderView()
    {
        RuntimeViewFrame frame = RuntimeViewFrame.Create(
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            640,
            480,
            0,
            cameraCut: true);

        Assert.True(frame.View.CameraCut);
    }
}
