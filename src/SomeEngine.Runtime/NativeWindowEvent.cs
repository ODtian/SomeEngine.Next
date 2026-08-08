using System.Numerics;

namespace SomeEngine.Runtime;

internal enum NativeWindowEventKind : byte
{
    Resized,
    DpiChanged,
    FocusChanged,
    KeyChanged,
    TextInput,
    MouseMoved,
    MouseLeft,
    MouseButtonChanged,
    MouseWheel,
}

internal enum NativeMouseButton : byte
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

internal readonly record struct NativeWindowEvent
{
    private NativeWindowEvent(NativeWindowEventKind kind)
    {
        Kind = kind;
    }

    internal NativeWindowEventKind Kind { get; }
    internal int Width { get; init; }
    internal int Height { get; init; }
    internal float DpiScale { get; init; }
    internal bool Focused { get; init; }
    internal int VirtualKey { get; init; }
    internal int ScanCode { get; init; }
    internal bool IsDown { get; init; }
    internal bool IsRepeat { get; init; }
    internal bool IsExtended { get; init; }
    internal char Utf16Character { get; init; }
    internal Vector2 MousePosition { get; init; }
    internal NativeMouseButton MouseButton { get; init; }
    internal Vector2 Wheel { get; init; }

    internal static NativeWindowEvent Resized(int width, int height) =>
        new(NativeWindowEventKind.Resized) { Width = width, Height = height };

    internal static NativeWindowEvent DpiChanged(float scale) =>
        new(NativeWindowEventKind.DpiChanged) { DpiScale = scale };

    internal static NativeWindowEvent FocusChanged(bool focused) =>
        new(NativeWindowEventKind.FocusChanged) { Focused = focused };

    internal static NativeWindowEvent KeyChanged(
        int virtualKey,
        int scanCode,
        bool isDown,
        bool repeat,
        bool extended) =>
        new(NativeWindowEventKind.KeyChanged)
        {
            VirtualKey = virtualKey,
            ScanCode = scanCode,
            IsDown = isDown,
            IsRepeat = repeat,
            IsExtended = extended,
        };

    internal static NativeWindowEvent TextInput(char character) =>
        new(NativeWindowEventKind.TextInput) { Utf16Character = character };

    internal static NativeWindowEvent MouseMoved(float x, float y) =>
        new(NativeWindowEventKind.MouseMoved) { MousePosition = new Vector2(x, y) };

    internal static NativeWindowEvent MouseLeft() => new(NativeWindowEventKind.MouseLeft);

    internal static NativeWindowEvent MouseButtonChanged(
        NativeMouseButton button,
        bool isDown,
        float x,
        float y) =>
        new(NativeWindowEventKind.MouseButtonChanged)
        {
            MouseButton = button,
            IsDown = isDown,
            MousePosition = new Vector2(x, y),
        };

    internal static NativeWindowEvent MouseWheel(float x, float y, float horizontal, float vertical) =>
        new(NativeWindowEventKind.MouseWheel)
        {
            MousePosition = new Vector2(x, y),
            Wheel = new Vector2(horizontal, vertical),
        };
}
