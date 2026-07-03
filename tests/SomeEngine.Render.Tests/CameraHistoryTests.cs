using System.Numerics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Tests;

public sealed class CameraHistoryTests
{
    [Fact]
    public void CommitStoresPreviousCameraMatrices()
    {
        var history = new CameraHistory();
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.UnitZ, Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.5f, 0.1f, 100.0f);
        Matrix4x4 motion = view * projection;

        history.Commit(view, projection, motion);

        Assert.True(history.HasPrevious);
        Assert.Equal(Matrix4x4.Transpose(view * projection), history.PrevViewProjT);
        Assert.Equal(view, history.PrevView);
        Assert.Equal(projection, history.PrevProj);
        Assert.Equal(motion, history.PrevMotionViewProj);
    }

    [Fact]
    public void ResetClearsPreviousCameraState()
    {
        var history = new CameraHistory();
        history.Commit(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity);

        history.Reset();

        Assert.False(history.HasPrevious);
        Assert.Equal(Matrix4x4.Identity, history.PrevViewProjT);
        Assert.Equal(Matrix4x4.Identity, history.PrevView);
        Assert.Equal(Matrix4x4.Identity, history.PrevProj);
    }
}
