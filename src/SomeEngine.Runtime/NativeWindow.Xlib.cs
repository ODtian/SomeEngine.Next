using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SomeEngine.Runtime;

internal sealed partial class NativeWindow
{
    private nint _xDisplay;
    private nuint _xDeleteMessage;

    private void CreateXlibWindow(string title, int width, int height, bool visible)
    {
        XInitThreads();
        string? display = Environment.GetEnvironmentVariable("SOMEENGINE_DISPLAY") ?? Environment.GetEnvironmentVariable("DISPLAY");
        _xDisplay = XOpenDisplay(display);
        if (_xDisplay == 0) throw new InvalidOperationException("XOpenDisplay could not connect to " + display);
        int screen = XDefaultScreen(_xDisplay);
        nuint window = XCreateSimpleWindow(_xDisplay, XRootWindow(_xDisplay, screen), 0, 0,
            (uint)width, (uint)height, 0, 0, 0);
        Handle = (nint)window;
        ClientWidth = width; ClientHeight = height; HasFocus = true;
        XStoreName(_xDisplay, window, title);
        _xDeleteMessage = XInternAtom(_xDisplay, "WM_DELETE_WINDOW", 0);
        XSetWMProtocols(_xDisplay, window, ref _xDeleteMessage, 1);
        const nint events = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 3) | (1 << 5) | (1 << 6) | (1 << 15) | (1 << 17) | (1 << 21);
        XSelectInput(_xDisplay, window, events);
        if (visible)
        {
            XMapWindow(_xDisplay, window);
            VisibleTimestamp = Stopwatch.GetTimestamp();
        }
        XFlush(_xDisplay);
    }

    private void ResizeXlibWindow(int width, int height)
    {
        XResizeWindow(_xDisplay, (nuint)Handle, (uint)width, (uint)height);
        XFlush(_xDisplay);
    }

    private bool PumpXlibEvents()
    {
        while (XPending(_xDisplay) > 0)
        {
            XNextEvent(_xDisplay, out XlibEvent message);
            switch (message.Type)
            {
                case 2:
                case 3:
                    bool down = message.Type == 2;
                    nuint symbol = XLookupKeysym(ref message, 0);
                    int key = XlibVirtualKey(symbol);
                    if (key != 0) _events.Enqueue(NativeWindowEvent.KeyChanged(key, (int)message.KeyOrButton, down, false, false));
                    if (down)
                    {
                        byte[] text = new byte[64];
                        int count = XLookupString(ref message, text, text.Length, out _, 0);
                        if (count > 0) _events.Enqueue(NativeWindowEvent.TextCommitted(Encoding.UTF8.GetString(text, 0, count)));
                    }
                    break;
                case 4:
                case 5:
                    bool pressed = message.Type == 4;
                    uint button = message.KeyOrButton;
                    if (button is >= 4 and <= 7)
                    {
                        if (pressed) _events.Enqueue(NativeWindowEvent.MouseWheel(message.X, message.Y,
                            button == 6 ? -1 : button == 7 ? 1 : 0, button == 4 ? 1 : button == 5 ? -1 : 0));
                    }
                    else
                    {
                        NativeMouseButton mapped = button switch { 1 => NativeMouseButton.Left, 2 => NativeMouseButton.Middle,
                            3 => NativeMouseButton.Right, 8 => NativeMouseButton.X1, _ => NativeMouseButton.X2 };
                        _events.Enqueue(NativeWindowEvent.MouseButtonChanged(mapped, pressed, message.X, message.Y));
                    }
                    break;
                case 6: _events.Enqueue(NativeWindowEvent.MouseMoved(message.X, message.Y)); break;
                case 8: _events.Enqueue(NativeWindowEvent.MouseLeft()); break;
                case 9:
                case 10:
                    HasFocus = message.Type == 9;
                    _events.Enqueue(NativeWindowEvent.FocusChanged(HasFocus));
                    break;
                case 17: _closeRequested = true; Handle = 0; break;
                case 22:
                    if (message.Width != ClientWidth || message.Height != ClientHeight)
                    {
                        ClientWidth = message.Width; ClientHeight = message.Height;
                        _events.Enqueue(NativeWindowEvent.Resized(ClientWidth, ClientHeight));
                    }
                    break;
                case 33:
                    if ((nuint)message.Data0 == _xDeleteMessage) _closeRequested = true;
                    break;
            }
        }
        return !_closeRequested && Handle != 0;
    }

    private static int XlibVirtualKey(nuint symbol)
    {
        if (symbol is >= 0x20 and <= 0x7e) return char.ToUpperInvariant((char)symbol);
        if (symbol is >= 0xffbe and <= 0xffc9) return 0x70 + (int)(symbol - 0xffbe);
        return symbol switch
        {
            0xff08 => 0x08, 0xff09 => 0x09, 0xff0d => 0x0d, 0xff1b => 0x1b,
            0xff50 => 0x24, 0xff51 => 0x25, 0xff52 => 0x26, 0xff53 => 0x27, 0xff54 => 0x28,
            0xff55 => 0x21, 0xff56 => 0x22, 0xff57 => 0x23, 0xff63 => 0x2d, 0xffff => 0x2e,
            0xffe1 or 0xffe2 => 0x10, 0xffe3 or 0xffe4 => 0x11, 0xffe9 or 0xffea => 0x12,
            _ => 0,
        };
    }

    private void WaitForXlibEvents(TimeSpan timeout)
    {
        var descriptor = new XlibPoll { File = XConnectionNumber(_xDisplay), Events = 1 };
        Poll(ref descriptor, 1, (int)Math.Min(int.MaxValue, GetWaitMilliseconds(timeout)));
    }

    private void DestroyXlibWindow()
    {
        if (Handle != 0) XDestroyWindow(_xDisplay, (nuint)Handle);
        Handle = 0;
        if (_xDisplay != 0) XCloseDisplay(_xDisplay);
        _xDisplay = 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XlibEvent
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(56)] internal nint Data0;
        [FieldOffset(56)] internal int Width;
        [FieldOffset(60)] internal int Height;
        [FieldOffset(64)] internal int X;
        [FieldOffset(68)] internal int Y;
        [FieldOffset(84)] internal uint KeyOrButton;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XlibPoll { internal int File; internal short Events; internal short ReturnedEvents; }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial nint XOpenDisplay(string? display);
    [LibraryImport("libX11.so.6")] private static partial int XInitThreads();
    [LibraryImport("libX11.so.6")] private static partial int XDefaultScreen(nint display);
    [LibraryImport("libX11.so.6")] private static partial nuint XRootWindow(nint display, int screen);
    [LibraryImport("libX11.so.6")] private static partial nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial int XStoreName(nint display, nuint window, string title);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XInternAtom(nint display, string name, int onlyIfExists);
    [LibraryImport("libX11.so.6")] private static partial int XSetWMProtocols(nint display, nuint window, ref nuint protocols, int count);
    [LibraryImport("libX11.so.6")] private static partial int XSelectInput(nint display, nuint window, nint events);
    [LibraryImport("libX11.so.6")] private static partial int XMapWindow(nint display, nuint window);
    [LibraryImport("libX11.so.6")] private static partial int XFlush(nint display);
    [LibraryImport("libX11.so.6")] private static partial int XResizeWindow(nint display, nuint window, uint width, uint height);
    [LibraryImport("libX11.so.6")] private static partial int XPending(nint display);
    [LibraryImport("libX11.so.6")] private static partial int XNextEvent(nint display, out XlibEvent message);
    [LibraryImport("libX11.so.6")] private static partial nuint XLookupKeysym(ref XlibEvent message, int index);
    [DllImport("libX11.so.6")] private static extern int XLookupString(ref XlibEvent message, [Out] byte[] buffer, int length, out nuint symbol, nint compose);
    [LibraryImport("libX11.so.6")] private static partial int XConnectionNumber(nint display);
    [LibraryImport("libX11.so.6")] private static partial int XDestroyWindow(nint display, nuint window);
    [LibraryImport("libX11.so.6")] private static partial int XCloseDisplay(nint display);
    [LibraryImport("libc", EntryPoint = "poll")] private static partial int Poll(ref XlibPoll descriptors, nuint count, int timeout);
}
