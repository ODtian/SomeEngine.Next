using System.Numerics;
using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeCameraTests
{
    [Fact]
    public void MovementAndMouseLookConsumeInputUnlessUiCapturesIt()
    {
        var camera = new RuntimeCamera(
            new Vector3(0.0f, 0.0f, 5.0f),
            Vector3.Zero,
            Vector3.UnitY);
        var input = new RuntimeInput();
        input.BeginFrame();
        input.Process(NativeWindowEvent.KeyChanged(
            RuntimeInput.KeyW,
            scanCode: 17,
            isDown: true,
            repeat: false,
            extended: false));

        camera.Update(input, 1.0f, captureKeyboard: false, captureMouse: false);

        Assert.InRange(camera.Position.Z, 4.6999f, 4.7001f);
        Vector3 movedPosition = camera.Position;
        camera.Update(input, 1.0f, captureKeyboard: true, captureMouse: false);
        Assert.Equal(movedPosition, camera.Position);

        input.BeginFrame();
        input.Process(NativeWindowEvent.MouseMoved(100.0f, 100.0f));
        input.Process(NativeWindowEvent.MouseMoved(120.0f, 100.0f));
        input.Process(NativeWindowEvent.MouseButtonChanged(
            NativeMouseButton.Right,
            isDown: true,
            120.0f,
            100.0f));
        Matrix4x4 beforeLook = camera.View;

        camera.Update(input, 1.0f / 60.0f, captureKeyboard: true, captureMouse: false);

        Assert.NotEqual(beforeLook, camera.View);
    }
}
