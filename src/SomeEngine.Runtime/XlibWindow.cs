using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.Graphics;

namespace SomeEngine.Runtime;

internal sealed unsafe class XlibWindow : NativeWindow
{
    private const string X11 = "libX11.so.6";
    private const long InputEvents = (1L << 0) | (1L << 1) | (1L << 2) | (1L << 3) |
        (1L << 5) | (1L << 6) | (1L << 17) | (1L << 21);
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int ButtonPress = 4;
    private const int ButtonRelease = 5;
    private const int MotionNotify = 6;
    private const int LeaveNotify = 8;
    private const int FocusIn = 9;
    private const int FocusOut = 10;
    private const int DestroyNotify = 17;
    private const int UnmapNotify = 18;
    private const int MapNotify = 19;
    private const int ConfigureNotify = 22;
    private const int ClientMessage = 33;

    private readonly Queue<NativeWindowEvent> _events = [];
    private readonly bool[] _keys = new bool[256];
    private readonly nuint _deleteWindow;
    private readonly int _connection;
    private nint _inputMethod;
    private nint _inputContext;
    private int _pressedMouseButtons;
    private bool _closeRequested;

    internal XlibWindow(string title, int width, int height, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _ = XInitThreads();
        DisplayHandle = XOpenDisplay(0);
        if (DisplayHandle == 0)
            throw new InvalidOperationException("XOpenDisplay requires an available X11 display.");
        try
        {
            int screen = XDefaultScreen(DisplayHandle);
            Handle = (nint)XCreateSimpleWindow(DisplayHandle, XRootWindow(DisplayHandle, screen),
                0, 0, (uint)width, (uint)height, 0, 0, 0);
            if (Handle == 0)
                throw new InvalidOperationException("XCreateSimpleWindow failed.");
            _ = XStoreName(DisplayHandle, (nuint)Handle, title);
            _ = XSelectInput(DisplayHandle, (nuint)Handle, (nint)InputEvents);
            _deleteWindow = XInternAtom(DisplayHandle, "WM_DELETE_WINDOW", false);
            nuint deleteWindow = _deleteWindow;
            _ = XSetWMProtocols(DisplayHandle, (nuint)Handle, &deleteWindow, 1);
            _ = XkbSetDetectableAutoRepeat(DisplayHandle, true, out _);
            _connection = XConnectionNumber(DisplayHandle);
            ClientWidth = width;
            ClientHeight = height;
            DpiScale = ReadDpiScale();
            CreateTextInput();
            if (visible) _ = XMapWindow(DisplayHandle, (nuint)Handle);
            _ = XSync(DisplayHandle, false);
            if (visible)
            {
                VisibleTimestamp = Stopwatch.GetTimestamp();
                _ = XSetInputFocus(DisplayHandle, (nuint)Handle, 1, 0);
            }
            _ = XFlush(DisplayHandle);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal override NativeWindowType Type => NativeWindowType.Xlib;
    internal override bool CloseRequested => _closeRequested;

    internal override void SetClientSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ = XResizeWindow(DisplayHandle, (nuint)Handle, (uint)width, (uint)height);
        _ = XSync(DisplayHandle, false);
    }

    internal override bool PumpMessages()
    {
        if (_closeRequested || Handle == 0)
            return false;
        while (XPending(DisplayHandle) > 0)
        {
            _ = XNextEvent(DisplayHandle, out XEvent windowEvent);
            if (XFilterEvent(ref windowEvent, (nuint)Handle))
                continue;
            switch (windowEvent.Type)
            {
                case ConfigureNotify:
                    XConfigureEvent size = windowEvent.Configure;
                    if (size.Width != ClientWidth || size.Height != ClientHeight)
                    {
                        ClientWidth = size.Width;
                        ClientHeight = size.Height;
                        _events.Enqueue(NativeWindowEvent.Resized(size.Width, size.Height));
                    }
                    break;
                case FocusIn:
                    HasFocus = true;
                    if (_inputContext != 0) XSetICFocus(_inputContext);
                    _events.Enqueue(NativeWindowEvent.FocusChanged(true));
                    break;
                case FocusOut:
                    HasFocus = false;
                    _keys.AsSpan().Clear();
                    _pressedMouseButtons = 0;
                    _ = XUngrabPointer(DisplayHandle, 0);
                    if (_inputContext != 0) XUnsetICFocus(_inputContext);
                    _events.Enqueue(NativeWindowEvent.FocusChanged(false));
                    break;
                case KeyPress:
                case KeyRelease:
                    EnqueueKey(ref windowEvent.Input, windowEvent.Type == KeyPress);
                    break;
                case ButtonPress:
                case ButtonRelease:
                    EnqueueMouseButton(windowEvent.Input, windowEvent.Type == ButtonPress);
                    break;
                case MotionNotify:
                    _events.Enqueue(NativeWindowEvent.MouseMoved(windowEvent.Input.X, windowEvent.Input.Y));
                    break;
                case LeaveNotify:
                    _events.Enqueue(NativeWindowEvent.MouseLeft());
                    break;
                case UnmapNotify:
                    IsMinimized = true;
                    break;
                case MapNotify:
                    IsMinimized = false;
                    break;
                case ClientMessage when windowEvent.Client.Data0 == (nint)_deleteWindow:
                    _closeRequested = true;
                    break;
                case DestroyNotify:
                    Handle = 0;
                    _closeRequested = true;
                    break;
            }
        }
        return !_closeRequested;
    }

    internal override bool TryReadEvent(out NativeWindowEvent windowEvent) =>
        _events.TryDequeue(out windowEvent);

    internal override void RequestClose() => _closeRequested = true;

    internal override void WaitForEvents(TimeSpan timeout)
    {
        if (XPending(DisplayHandle) > 0) return;
        PollDescriptor descriptor = new() { File = _connection, Events = 1 };
        int milliseconds = timeout == Timeout.InfiniteTimeSpan ? -1 :
            (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 0, int.MaxValue);
        if (poll(&descriptor, 1, milliseconds) < 0 && Marshal.GetLastPInvokeError() != 4)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Waiting for X11 events failed.");
    }

    internal override void WaitForEvents(WaitHandle completion, TimeSpan timeout)
    {
        if (XPending(DisplayHandle) > 0) return;
        TimeSpan interval = TimeSpan.FromMilliseconds(2);
        _ = completion.WaitOne(timeout == Timeout.InfiniteTimeSpan || timeout > interval ? interval : timeout);
    }

    public override void Dispose()
    {
        if (DisplayHandle == 0) return;
        if (_inputContext != 0) XDestroyIC(_inputContext);
        if (_inputMethod != 0) _ = XCloseIM(_inputMethod);
        if (Handle != 0) _ = XDestroyWindow(DisplayHandle, (nuint)Handle);
        _ = XCloseDisplay(DisplayHandle);
        _inputContext = 0;
        _inputMethod = 0;
        Handle = 0;
        DisplayHandle = 0;
    }

    private float ReadDpiScale()
    {
        string? resources = Marshal.PtrToStringUTF8(XResourceManagerString(DisplayHandle));
        if (resources is null) return 1;
        foreach (string line in resources.Split('\n'))
        {
            if (line.StartsWith("Xft.dpi:", StringComparison.Ordinal) &&
                float.TryParse(line.AsSpan(8), CultureInfo.InvariantCulture, out float dpi))
                return dpi / 96;
        }
        return 1;
    }

    private void CreateTextInput()
    {
        _ = setlocale(0, "");
        _ = XSetLocaleModifiers("");
        _inputMethod = XOpenIM(DisplayHandle, 0, 0, 0);
        if (_inputMethod != 0)
            _inputContext = XCreateIC(_inputMethod, "inputStyle", 0x408,
                "clientWindow", (nuint)Handle, "focusWindow", (nuint)Handle, 0);
    }

    private void EnqueueKey(ref XInputEvent input, bool pressed)
    {
        nuint symbol = XLookupKeysym(ref input, 0);
        int key = GetVirtualKey(symbol);
        bool repeat = _keys[input.ButtonOrKeyCode] && pressed;
        _keys[input.ButtonOrKeyCode] = pressed;
        _events.Enqueue(NativeWindowEvent.KeyChanged(key, (int)input.ButtonOrKeyCode,
            pressed, repeat, symbol is >= 0xFF50 and <= 0xFF58));
        if (!pressed) return;

        Span<byte> text = stackalloc byte[64];
        int count;
        fixed (byte* bytes = text)
        {
            if (_inputContext == 0)
                count = XLookupString(ref input, bytes, text.Length, out _, 0);
            else
            {
                count = Xutf8LookupString(_inputContext, ref input, bytes, text.Length, out _, out int status);
                if (status == -1)
                {
                    byte[] expanded = new byte[count];
                    fixed (byte* expandedBytes = expanded)
                        count = Xutf8LookupString(_inputContext, ref input, expandedBytes, expanded.Length, out _, out _);
                    _events.Enqueue(NativeWindowEvent.TextCommitted(Encoding.UTF8.GetString(expanded, 0, count)));
                    return;
                }
            }
        }
        if (count > 0)
        {
            string value = (_inputContext == 0 ? Encoding.Latin1 : Encoding.UTF8).GetString(text[..count]);
            if (!char.IsControl(value[0])) _events.Enqueue(NativeWindowEvent.TextCommitted(value));
        }
    }

    private void EnqueueMouseButton(in XInputEvent input, bool pressed)
    {
        uint button = input.ButtonOrKeyCode;
        if (button is >= 4 and <= 7)
        {
            if (pressed)
                _events.Enqueue(NativeWindowEvent.MouseWheel(input.X, input.Y,
                    button == 6 ? -1 : button == 7 ? 1 : 0,
                    button == 4 ? 1 : button == 5 ? -1 : 0));
            return;
        }
        NativeMouseButton? mouseButton = button switch
        {
            1 => NativeMouseButton.Left,
            2 => NativeMouseButton.Middle,
            3 => NativeMouseButton.Right,
            8 => NativeMouseButton.X1,
            9 => NativeMouseButton.X2,
            _ => null,
        };
        if (mouseButton is not { } value) return;
        int mask = 1 << (int)value;
        if (pressed)
        {
            if (_pressedMouseButtons == 0)
                _ = XGrabPointer(DisplayHandle, (nuint)Handle, true, (1 << 2) | (1 << 3) | (1 << 6),
                    1, 1, 0, 0, 0);
            _pressedMouseButtons |= mask;
        }
        else
        {
            _pressedMouseButtons &= ~mask;
            if (_pressedMouseButtons == 0) _ = XUngrabPointer(DisplayHandle, 0);
        }
        _events.Enqueue(NativeWindowEvent.MouseButtonChanged(value, pressed, input.X, input.Y));
    }

    private static int GetVirtualKey(nuint symbol) => symbol switch
    {
        >= 0x61 and <= 0x7A => (int)symbol - 0x20,
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => (int)symbol,
        >= 0xFFB0 and <= 0xFFB9 => (int)symbol - 0xFFB0 + 0x60,
        >= 0xFFBE and <= 0xFFD5 => (int)symbol - 0xFFBE + 0x70,
        0xFF08 => RuntimeInput.KeyBackspace,
        0xFF09 or 0xFE20 => RuntimeInput.KeyTab,
        0xFF0D or 0xFF8D => RuntimeInput.KeyEnter,
        0xFF13 => 0x13,
        0xFF14 => 0x91,
        0xFF1B => RuntimeInput.KeyEscape,
        0x20 => RuntimeInput.KeySpace,
        0xFF50 => RuntimeInput.KeyHome,
        0xFF51 => RuntimeInput.KeyLeft,
        0xFF52 => RuntimeInput.KeyUp,
        0xFF53 => RuntimeInput.KeyRight,
        0xFF54 => RuntimeInput.KeyDown,
        0xFF55 => RuntimeInput.KeyPageUp,
        0xFF56 => RuntimeInput.KeyPageDown,
        0xFF57 => RuntimeInput.KeyEnd,
        0xFF61 => 0x2C,
        0xFF63 => RuntimeInput.KeyInsert,
        0xFFFF => RuntimeInput.KeyDelete,
        0xFFE1 => RuntimeInput.KeyLeftShift,
        0xFFE2 => RuntimeInput.KeyRightShift,
        0xFFE3 => RuntimeInput.KeyLeftControl,
        0xFFE4 => RuntimeInput.KeyRightControl,
        0xFFE5 => 0x14,
        0xFFE9 => RuntimeInput.KeyLeftMenu,
        0xFFEA => RuntimeInput.KeyRightMenu,
        0xFFEB => 0x5B,
        0xFFEC => 0x5C,
        0xFF67 => 0x5D,
        0xFF7F => 0x90,
        0xFFAA => 0x6A,
        0xFFAB => 0x6B,
        0xFFAD => 0x6D,
        0xFFAE => 0x6E,
        0xFFAF => 0x6F,
        0x3B => 0xBA,
        0x3D => 0xBB,
        0x2C => 0xBC,
        0x2D => 0xBD,
        0x2E => 0xBE,
        0x2F => 0xBF,
        0x60 => 0xC0,
        0x5B => 0xDB,
        0x5C => 0xDC,
        0x5D => 0xDD,
        0x27 => 0xDE,
        _ => 0,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputEvent
    {
        internal int Type;
        internal nuint Serial;
        internal int Sent;
        internal nint Display;
        internal nuint Window;
        internal nuint Root;
        internal nuint Subwindow;
        internal nuint Time;
        internal int X;
        internal int Y;
        internal int RootX;
        internal int RootY;
        internal uint State;
        internal uint ButtonOrKeyCode;
        internal int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XConfigureEvent
    {
        internal int Type;
        internal nuint Serial;
        internal int Sent;
        internal nint Display;
        internal nuint Event;
        internal nuint Window;
        internal int X;
        internal int Y;
        internal int Width;
        internal int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientEvent
    {
        internal int Type;
        internal nuint Serial;
        internal int Sent;
        internal nint Display;
        internal nuint Window;
        internal nuint MessageType;
        internal int Format;
        internal nint Data0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(0)] internal XInputEvent Input;
        [FieldOffset(0)] internal XConfigureEvent Configure;
        [FieldOffset(0)] internal XClientEvent Client;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int File;
        internal short Events;
        internal short ReturnedEvents;
    }

    [DllImport(X11)] private static extern int XInitThreads();
    [DllImport(X11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] private static extern int XCloseDisplay(nint display);
    [DllImport(X11)] private static extern int XDefaultScreen(nint display);
    [DllImport(X11)] private static extern nuint XRootWindow(nint display, int screen);
    [DllImport(X11)] private static extern nuint XCreateSimpleWindow(nint display, nuint parent,
        int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport(X11)] private static extern int XStoreName(nint display, nuint window, string title);
    [DllImport(X11)] private static extern int XSelectInput(nint display, nuint window, nint mask);
    [DllImport(X11)] private static extern nuint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport(X11)] private static extern int XSetWMProtocols(nint display, nuint window, nuint* protocols, int count);
    [DllImport(X11)] private static extern bool XkbSetDetectableAutoRepeat(nint display, bool detectable, out bool supported);
    [DllImport(X11)] private static extern int XConnectionNumber(nint display);
    [DllImport(X11)] private static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] private static extern int XSetInputFocus(nint display, nuint window, int revert, nuint time);
    [DllImport(X11)] private static extern int XFlush(nint display);
    [DllImport(X11)] private static extern int XSync(nint display, bool discard);
    [DllImport(X11)] private static extern int XResizeWindow(nint display, nuint window, uint width, uint height);
    [DllImport(X11)] private static extern int XPending(nint display);
    [DllImport(X11)] private static extern int XNextEvent(nint display, out XEvent windowEvent);
    [DllImport(X11)] private static extern bool XFilterEvent(ref XEvent windowEvent, nuint window);
    [DllImport(X11)] private static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X11)] private static extern nint XResourceManagerString(nint display);
    [DllImport(X11)] private static extern nint XSetLocaleModifiers(string modifiers);
    [DllImport(X11)] private static extern nint XOpenIM(nint display, nint database, nint resourceName, nint resourceClass);
    [DllImport(X11)] private static extern int XCloseIM(nint inputMethod);
    [DllImport(X11)] private static extern nint XCreateIC(nint inputMethod, string styleName, nuint style,
        string windowName, nuint window, string focusName, nuint focus, nint end);
    [DllImport(X11)] private static extern void XDestroyIC(nint inputContext);
    [DllImport(X11)] private static extern void XSetICFocus(nint inputContext);
    [DllImport(X11)] private static extern void XUnsetICFocus(nint inputContext);
    [DllImport(X11)] private static extern nuint XLookupKeysym(ref XInputEvent key, int index);
    [DllImport(X11)] private static extern int XLookupString(ref XInputEvent key, byte* buffer,
        int capacity, out nuint symbol, nint compose);
    [DllImport(X11)] private static extern int Xutf8LookupString(nint inputContext, ref XInputEvent key,
        byte* buffer, int capacity, out nuint symbol, out int status);
    [DllImport(X11)] private static extern int XGrabPointer(nint display, nuint window, bool ownEvents,
        uint events, int pointerMode, int keyboardMode, nuint confineTo, nuint pointerImage, nuint time);
    [DllImport(X11)] private static extern int XUngrabPointer(nint display, nuint time);
    [DllImport("libc")] private static extern nint setlocale(int category, string locale);
    [DllImport("libc", SetLastError = true)] private static extern int poll(PollDescriptor* descriptors,
        nuint count, int milliseconds);
}
