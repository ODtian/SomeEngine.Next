using System.Numerics;
using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class RuntimeInputTests
{
    [Fact]
    public void KeyStateTracksPressHoldReleaseAndFocusLoss()
    {
        var input = new RuntimeInput();

        input.BeginFrame();
        input.Process(NativeWindowEvent.FocusChanged(true));
        input.Process(NativeWindowEvent.KeyChanged(
            RuntimeInput.KeyW,
            scanCode: 17,
            isDown: true,
            repeat: false,
            extended: false));

        Assert.True(input.HasFocus);
        Assert.True(input.IsKeyDown(RuntimeInput.KeyW));
        Assert.True(input.WasKeyPressed(RuntimeInput.KeyW));
        Assert.False(input.WasKeyReleased(RuntimeInput.KeyW));

        input.BeginFrame();

        Assert.True(input.IsKeyDown(RuntimeInput.KeyW));
        Assert.False(input.WasKeyPressed(RuntimeInput.KeyW));

        input.Process(NativeWindowEvent.FocusChanged(false));

        Assert.False(input.HasFocus);
        Assert.False(input.IsKeyDown(RuntimeInput.KeyW));
        Assert.True(input.WasKeyReleased(RuntimeInput.KeyW));
    }

    [Fact]
    public void MouseStateAccumulatesMotionWheelAndButtonTransitionsPerFrame()
    {
        var input = new RuntimeInput();

        input.BeginFrame();
        input.Process(NativeWindowEvent.MouseMoved(10.0f, 20.0f));
        input.Process(NativeWindowEvent.MouseMoved(13.0f, 16.0f));
        input.Process(NativeWindowEvent.MouseButtonChanged(
            NativeMouseButton.Right,
            isDown: true,
            13.0f,
            16.0f));
        input.Process(NativeWindowEvent.MouseWheel(13.0f, 16.0f, 1.0f, -2.0f));

        Assert.Equal(new Vector2(13.0f, 16.0f), input.MousePosition);
        Assert.Equal(new Vector2(3.0f, -4.0f), input.MouseDelta);
        Assert.Equal(new Vector2(1.0f, -2.0f), input.MouseWheel);
        Assert.True(input.IsMouseButtonDown(NativeMouseButton.Right));
        Assert.True(input.WasMouseButtonPressed(NativeMouseButton.Right));

        input.BeginFrame();

        Assert.Equal(Vector2.Zero, input.MouseDelta);
        Assert.Equal(Vector2.Zero, input.MouseWheel);
        Assert.True(input.IsMouseButtonDown(NativeMouseButton.Right));
        Assert.False(input.WasMouseButtonPressed(NativeMouseButton.Right));

        input.Process(NativeWindowEvent.MouseButtonChanged(
            NativeMouseButton.Right,
            isDown: false,
            13.0f,
            16.0f));

        Assert.False(input.IsMouseButtonDown(NativeMouseButton.Right));
        Assert.True(input.WasMouseButtonReleased(NativeMouseButton.Right));
    }
}
