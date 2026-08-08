using System.Numerics;

namespace SomeEngine.Runtime;

internal sealed class RuntimeInput
{
    internal const int KeyBackspace = 0x08;
    internal const int KeyTab = 0x09;
    internal const int KeyEnter = 0x0D;
    internal const int KeyShift = 0x10;
    internal const int KeyControl = 0x11;
    internal const int KeyMenu = 0x12;
    internal const int KeyEscape = 0x1B;
    internal const int KeySpace = 0x20;
    internal const int KeyPageUp = 0x21;
    internal const int KeyPageDown = 0x22;
    internal const int KeyEnd = 0x23;
    internal const int KeyHome = 0x24;
    internal const int KeyLeft = 0x25;
    internal const int KeyUp = 0x26;
    internal const int KeyRight = 0x27;
    internal const int KeyDown = 0x28;
    internal const int KeyInsert = 0x2D;
    internal const int KeyDelete = 0x2E;
    internal const int Key0 = 0x30;
    internal const int KeyA = 0x41;
    internal const int KeyC = 0x43;
    internal const int KeyD = 0x44;
    internal const int KeyS = 0x53;
    internal const int KeyV = 0x56;
    internal const int KeyW = 0x57;
    internal const int KeyX = 0x58;
    internal const int KeyY = 0x59;
    internal const int KeyZ = 0x5A;
    internal const int KeyF1 = 0x70;
    internal const int KeyLeftShift = 0xA0;
    internal const int KeyRightShift = 0xA1;
    internal const int KeyLeftControl = 0xA2;
    internal const int KeyRightControl = 0xA3;
    internal const int KeyLeftMenu = 0xA4;
    internal const int KeyRightMenu = 0xA5;

    private readonly bool[] _keys = new bool[256];
    private readonly bool[] _pressedKeys = new bool[256];
    private readonly bool[] _releasedKeys = new bool[256];
    private readonly bool[] _mouseButtons = new bool[5];
    private readonly bool[] _pressedMouseButtons = new bool[5];
    private readonly bool[] _releasedMouseButtons = new bool[5];
    private bool _hasMousePosition;

    internal Vector2 MousePosition { get; private set; }

    internal Vector2 MouseDelta { get; private set; }

    internal Vector2 MouseWheel { get; private set; }

    internal bool HasFocus { get; private set; }

    internal void BeginFrame()
    {
        Array.Clear(_pressedKeys);
        Array.Clear(_releasedKeys);
        Array.Clear(_pressedMouseButtons);
        Array.Clear(_releasedMouseButtons);
        MouseDelta = Vector2.Zero;
        MouseWheel = Vector2.Zero;
    }

    internal void Process(in NativeWindowEvent windowEvent)
    {
        switch (windowEvent.Kind)
        {
            case NativeWindowEventKind.FocusChanged:
                HasFocus = windowEvent.Focused;
                if (!HasFocus)
                    ReleaseAll();
                break;
            case NativeWindowEventKind.KeyChanged:
                SetKey(windowEvent.VirtualKey, windowEvent.IsDown);
                break;
            case NativeWindowEventKind.MouseMoved:
                SetMousePosition(windowEvent.MousePosition);
                break;
            case NativeWindowEventKind.MouseLeft:
                _hasMousePosition = false;
                break;
            case NativeWindowEventKind.MouseButtonChanged:
                SetMousePosition(windowEvent.MousePosition);
                SetMouseButton(windowEvent.MouseButton, windowEvent.IsDown);
                break;
            case NativeWindowEventKind.MouseWheel:
                SetMousePosition(windowEvent.MousePosition);
                MouseWheel += windowEvent.Wheel;
                break;
        }
    }

    internal bool IsKeyDown(int virtualKey) =>
        IsValidKey(virtualKey) && _keys[virtualKey];

    internal bool WasKeyPressed(int virtualKey) =>
        IsValidKey(virtualKey) && _pressedKeys[virtualKey];

    internal bool WasKeyReleased(int virtualKey) =>
        IsValidKey(virtualKey) && _releasedKeys[virtualKey];

    internal bool IsMouseButtonDown(NativeMouseButton button) =>
        _mouseButtons[checked((int)button)];

    internal bool WasMouseButtonPressed(NativeMouseButton button) =>
        _pressedMouseButtons[checked((int)button)];

    internal bool WasMouseButtonReleased(NativeMouseButton button) =>
        _releasedMouseButtons[checked((int)button)];

    private void SetKey(int virtualKey, bool isDown)
    {
        if (!IsValidKey(virtualKey))
            return;
        bool previous = _keys[virtualKey];
        _keys[virtualKey] = isDown;
        if (isDown && !previous)
            _pressedKeys[virtualKey] = true;
        else if (!isDown && previous)
            _releasedKeys[virtualKey] = true;
    }

    private void SetMouseButton(NativeMouseButton button, bool isDown)
    {
        int index = checked((int)button);
        bool previous = _mouseButtons[index];
        _mouseButtons[index] = isDown;
        if (isDown && !previous)
            _pressedMouseButtons[index] = true;
        else if (!isDown && previous)
            _releasedMouseButtons[index] = true;
    }

    private void SetMousePosition(Vector2 position)
    {
        if (_hasMousePosition)
            MouseDelta += position - MousePosition;
        MousePosition = position;
        _hasMousePosition = true;
    }

    private void ReleaseAll()
    {
        for (int index = 0; index < _keys.Length; index++)
        {
            if (_keys[index])
                _releasedKeys[index] = true;
            _keys[index] = false;
        }
        for (int index = 0; index < _mouseButtons.Length; index++)
        {
            if (_mouseButtons[index])
                _releasedMouseButtons[index] = true;
            _mouseButtons[index] = false;
        }
        _hasMousePosition = false;
    }

    private static bool IsValidKey(int virtualKey) =>
        (uint)virtualKey < 256u;
}
