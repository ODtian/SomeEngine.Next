using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Runtime;

internal sealed class NativeWindow : IDisposable
{
    private const string WindowClassName = "SomeEngine.Runtime.Window";
    private const uint ClassHorizontalRedraw = 0x0002;
    private const uint ClassVerticalRedraw = 0x0001;
    private const uint WindowStyleOverlapped = 0x00CF0000;
    private const uint WindowStyleVisible = 0x10000000;
    private const uint PeekRemove = 0x0001;
    private const uint QueueAllInput = 0x04FF;
    private const uint WaitInputAvailable = 0x0004;
    private const uint TrackMouseLeave = 0x00000002;
    private const uint SetPositionNoActivate = 0x0010;
    private const uint SetPositionNoZOrder = 0x0004;
    private const int ShowNormal = 5;
    private const int UseDefault = unchecked((int)0x80000000);
    private const int WindowUserData = -21;
    private const int ArrowCursor = 32512;
    private const int ErrorClassAlreadyExists = 1410;

    private const uint MessageCreate = 0x0001;
    private const uint MessageDestroy = 0x0002;
    private const uint MessageSize = 0x0005;
    private const uint MessageSetFocus = 0x0007;
    private const uint MessageKillFocus = 0x0008;
    private const uint MessagePaint = 0x000F;
    private const uint MessageClose = 0x0010;
    private const uint MessageQuit = 0x0012;
    private const uint MessageEraseBackground = 0x0014;
    private const uint MessageNcCreate = 0x0081;
    private const uint MessageNcDestroy = 0x0082;
    private const uint MessageKeyDown = 0x0100;
    private const uint MessageKeyUp = 0x0101;
    private const uint MessageCharacter = 0x0102;
    private const uint MessageSystemKeyDown = 0x0104;
    private const uint MessageSystemKeyUp = 0x0105;
    private const uint MessageSystemCharacter = 0x0106;
    private const uint MessageMouseMove = 0x0200;
    private const uint MessageLeftButtonDown = 0x0201;
    private const uint MessageLeftButtonUp = 0x0202;
    private const uint MessageRightButtonDown = 0x0204;
    private const uint MessageRightButtonUp = 0x0205;
    private const uint MessageMiddleButtonDown = 0x0207;
    private const uint MessageMiddleButtonUp = 0x0208;
    private const uint MessageMouseWheel = 0x020A;
    private const uint MessageXButtonDown = 0x020B;
    private const uint MessageXButtonUp = 0x020C;
    private const uint MessageMouseHorizontalWheel = 0x020E;
    private const uint MessageMouseLeave = 0x02A3;
    private const uint MessageDpiChanged = 0x02E0;

    private const nuint SizeMinimized = 1;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;
    private const int VirtualKeyLeftMenu = 0xA4;
    private const int VirtualKeyRightMenu = 0xA5;
    private const uint MapVirtualScanCodeToVirtualKey = 3;

    private static readonly object ClassRegistrationLock = new();
    private static readonly WindowProcedure WindowProcedureRoot = DispatchWindowMessage;
    private static bool _classRegistered;

    private readonly Queue<NativeWindowEvent> _events = [];
    private readonly GCHandle _selfHandle;
    private Exception? _dispatchFailure;
    private int _pressedMouseButtonMask;
    private bool _trackingMouseLeave;
    private bool _closeRequested;
    private bool _disposed;

    internal NativeWindow(string title, int clientWidth, int clientHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientHeight);

        EnsureWindowClass();
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        try
        {
            uint style = WindowStyleOverlapped | WindowStyleVisible;
            var outer = new NativeRect(0, 0, clientWidth, clientHeight);
            if (!AdjustWindowRectEx(ref outer, style, false, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustWindowRectEx failed.");

            Handle = CreateWindowExW(
                0,
                WindowClassName,
                title,
                style,
                UseDefault,
                UseDefault,
                checked(outer.Right - outer.Left),
                checked(outer.Bottom - outer.Top),
                0,
                0,
                GetModuleHandleW(null),
                GCHandle.ToIntPtr(_selfHandle));
            if (Handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");

            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
            uint dpi = GetDpiForWindow(Handle);
            DpiScale = dpi == 0 ? 1.0f : dpi / 96.0f;
            HasFocus = GetFocus() == Handle;
            _ = ShowWindow(Handle, ShowNormal);
            if (!UpdateWindow(Handle))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                    throw new Win32Exception(error, "UpdateWindow failed.");
            }
        }
        catch
        {
            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
            throw;
        }
    }

    internal nint Handle { get; private set; }

    internal int ClientWidth { get; private set; }

    internal int ClientHeight { get; private set; }

    internal float DpiScale { get; private set; } = 1.0f;

    internal bool HasFocus { get; private set; }

    internal bool IsMinimized { get; private set; }

    internal bool CloseRequested => _closeRequested;

    internal bool PumpMessages()
    {
        ThrowDispatchFailure();
        nint handle = Handle;
        if (handle == 0 || _closeRequested)
            return false;

        while (PeekMessageW(out NativeMessage message, 0, 0, 0, PeekRemove))
        {
            if (message.Message == MessageQuit)
            {
                _closeRequested = true;
                return false;
            }

            _ = TranslateMessage(in message);
            _ = DispatchMessageW(in message);
            ThrowDispatchFailure();
        }

        return !_closeRequested && Handle != 0 && IsWindow(handle);
    }

    internal bool TryReadEvent(out NativeWindowEvent windowEvent) =>
        _events.TryDequeue(out windowEvent);

    internal void RequestClose()
    {
        nint handle = Handle;
        if (handle != 0 && !_closeRequested)
            _ = PostMessageW(handle, MessageClose, 0, 0);
    }

    internal void WaitForEvents(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        uint milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? uint.MaxValue
            : checked((uint)Math.Clamp((long)Math.Ceiling(timeout.TotalMilliseconds), 0, uint.MaxValue - 1L));
        _ = MsgWaitForMultipleObjectsEx(
            0,
            0,
            milliseconds,
            QueueAllInput,
            WaitInputAvailable);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        nint handle = Handle;
        if (handle != 0 && IsWindow(handle) && !DestroyWindow(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed.");
        Handle = 0;
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _disposed = true;
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered)
            return;

        lock (ClassRegistrationLock)
        {
            if (_classRegistered)
                return;
            nint instance = GetModuleHandleW(null);
            var windowClass = new WindowClassEx
            {
                Size = checked((uint)Marshal.SizeOf<WindowClassEx>()),
                Style = ClassHorizontalRedraw | ClassVerticalRedraw,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureRoot),
                Instance = instance,
                Cursor = LoadCursorW(0, (nint)ArrowCursor),
                ClassName = WindowClassName,
            };
            ushort atom = RegisterClassExW(in windowClass);
            if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed.");
            _classRegistered = true;
        }
    }

    private static nint DispatchWindowMessage(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        NativeWindow? window = null;
        try
        {
            nint userData;
            if (message == MessageNcCreate)
            {
                userData = Marshal.ReadIntPtr(lParam);
                if (userData != 0)
                    _ = SetWindowLongPtrW(windowHandle, WindowUserData, userData);
            }
            else
            {
                userData = GetWindowLongPtrW(windowHandle, WindowUserData);
            }

            if (userData != 0)
                window = GCHandle.FromIntPtr(userData).Target as NativeWindow;
            return window?.ProcessWindowMessage(windowHandle, message, wParam, lParam)
                ?? DefWindowProcW(windowHandle, message, wParam, lParam);
        }
        catch (Exception failure)
        {
            if (window is not null)
            {
                window._dispatchFailure ??= failure;
                window._closeRequested = true;
            }
            PostQuitMessage(1);
            return 0;
        }
    }

    private nint ProcessWindowMessage(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case MessageCreate:
                return 0;
            case MessageClose:
                _closeRequested = true;
                if (!DestroyWindow(windowHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed while closing.");
                return 0;
            case MessageDestroy:
                _closeRequested = true;
                PostQuitMessage(0);
                return 0;
            case MessageNcDestroy:
            {
                nint result = DefWindowProcW(windowHandle, message, wParam, lParam);
                _ = SetWindowLongPtrW(windowHandle, WindowUserData, 0);
                Handle = 0;
                return result;
            }
            case MessageSize:
                IsMinimized = wParam == SizeMinimized;
                if (!IsMinimized)
                {
                    int width = UnsignedLowWord(lParam);
                    int height = UnsignedHighWord(lParam);
                    if (width > 0 && height > 0 && (width != ClientWidth || height != ClientHeight))
                    {
                        ClientWidth = width;
                        ClientHeight = height;
                        _events.Enqueue(NativeWindowEvent.Resized(width, height));
                    }
                }
                return 0;
            case MessageSetFocus:
                HasFocus = true;
                _events.Enqueue(NativeWindowEvent.FocusChanged(true));
                return 0;
            case MessageKillFocus:
                HasFocus = false;
                _pressedMouseButtonMask = 0;
                _events.Enqueue(NativeWindowEvent.FocusChanged(false));
                return 0;
            case MessageDpiChanged:
            {
                uint dpi = UnsignedLowWord(wParam);
                DpiScale = dpi == 0 ? 1.0f : dpi / 96.0f;
                if (lParam != 0)
                {
                    NativeRect suggested = Marshal.PtrToStructure<NativeRect>(lParam);
                    if (!SetWindowPos(
                            windowHandle,
                            0,
                            suggested.Left,
                            suggested.Top,
                            checked(suggested.Right - suggested.Left),
                            checked(suggested.Bottom - suggested.Top),
                            SetPositionNoActivate | SetPositionNoZOrder))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Applying WM_DPICHANGED failed.");
                    }
                }
                _events.Enqueue(NativeWindowEvent.DpiChanged(DpiScale));
                return 0;
            }
            case MessageKeyDown:
            case MessageSystemKeyDown:
                EnqueueKey(wParam, lParam, isDown: true);
                return message == MessageSystemKeyDown
                    ? DefWindowProcW(windowHandle, message, wParam, lParam)
                    : 0;
            case MessageKeyUp:
            case MessageSystemKeyUp:
                EnqueueKey(wParam, lParam, isDown: false);
                return message == MessageSystemKeyUp
                    ? DefWindowProcW(windowHandle, message, wParam, lParam)
                    : 0;
            case MessageCharacter:
            case MessageSystemCharacter:
                _events.Enqueue(NativeWindowEvent.TextInput(checked((char)wParam)));
                return 0;
            case MessageMouseMove:
                TrackMouseLeaveIfRequired(windowHandle);
                _events.Enqueue(NativeWindowEvent.MouseMoved(SignedLowWord(lParam), SignedHighWord(lParam)));
                return 0;
            case MessageMouseLeave:
                _trackingMouseLeave = false;
                _events.Enqueue(NativeWindowEvent.MouseLeft());
                return 0;
            case MessageLeftButtonDown:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Left, true, lParam);
                return 0;
            case MessageLeftButtonUp:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Left, false, lParam);
                return 0;
            case MessageRightButtonDown:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Right, true, lParam);
                return 0;
            case MessageRightButtonUp:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Right, false, lParam);
                return 0;
            case MessageMiddleButtonDown:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Middle, true, lParam);
                return 0;
            case MessageMiddleButtonUp:
                EnqueueMouseButton(windowHandle, NativeMouseButton.Middle, false, lParam);
                return 0;
            case MessageXButtonDown:
            case MessageXButtonUp:
            {
                NativeMouseButton button = UnsignedHighWord(wParam) == 1
                    ? NativeMouseButton.X1
                    : NativeMouseButton.X2;
                EnqueueMouseButton(windowHandle, button, message == MessageXButtonDown, lParam);
                return 1;
            }
            case MessageMouseWheel:
            case MessageMouseHorizontalWheel:
            {
                var point = new NativePoint(SignedLowWord(lParam), SignedHighWord(lParam));
                _ = ScreenToClient(windowHandle, ref point);
                float amount = SignedHighWord(wParam) / 120.0f;
                _events.Enqueue(message == MessageMouseWheel
                    ? NativeWindowEvent.MouseWheel(point.X, point.Y, 0.0f, amount)
                    : NativeWindowEvent.MouseWheel(point.X, point.Y, amount, 0.0f));
                return 0;
            }
            case MessageEraseBackground:
                return 1;
            case MessagePaint:
                _ = BeginPaint(windowHandle, out PaintStruct paint);
                _ = EndPaint(windowHandle, in paint);
                return 0;
            default:
                return DefWindowProcW(windowHandle, message, wParam, lParam);
        }
    }

    private void EnqueueKey(nuint wParam, nint lParam, bool isDown)
    {
        int virtualKey = checked((int)wParam);
        int scanCode = (int)((unchecked((ulong)lParam) >> 16) & 0xFF);
        bool extended = (unchecked((ulong)lParam) & (1UL << 24)) != 0;
        bool repeat = isDown && (unchecked((ulong)lParam) & (1UL << 30)) != 0;
        virtualKey = NormalizeVirtualKey(virtualKey, scanCode, extended);
        _events.Enqueue(NativeWindowEvent.KeyChanged(virtualKey, scanCode, isDown, repeat, extended));
    }

    private static int NormalizeVirtualKey(int virtualKey, int scanCode, bool extended) => virtualKey switch
    {
        VirtualKeyShift => checked((int)MapVirtualKeyW(checked((uint)scanCode), MapVirtualScanCodeToVirtualKey)),
        VirtualKeyControl => extended ? VirtualKeyRightControl : VirtualKeyLeftControl,
        VirtualKeyMenu => extended ? VirtualKeyRightMenu : VirtualKeyLeftMenu,
        _ => virtualKey,
    };

    private void EnqueueMouseButton(
        nint windowHandle,
        NativeMouseButton button,
        bool isDown,
        nint lParam)
    {
        int mask = 1 << (int)button;
        if (isDown)
        {
            _pressedMouseButtonMask |= mask;
            _ = SetCapture(windowHandle);
        }
        else
        {
            _pressedMouseButtonMask &= ~mask;
            if (_pressedMouseButtonMask == 0)
                _ = ReleaseCapture();
        }
        _events.Enqueue(NativeWindowEvent.MouseButtonChanged(
            button,
            isDown,
            SignedLowWord(lParam),
            SignedHighWord(lParam)));
    }

    private void TrackMouseLeaveIfRequired(nint windowHandle)
    {
        if (_trackingMouseLeave)
            return;
        var tracking = new MouseTrackingRequest
        {
            Size = checked((uint)Marshal.SizeOf<MouseTrackingRequest>()),
            Flags = TrackMouseLeave,
            Window = windowHandle,
        };
        if (TrackMouseEvent(ref tracking))
            _trackingMouseLeave = true;
    }

    private void ThrowDispatchFailure()
    {
        if (_dispatchFailure is not { } failure)
            return;
        _dispatchFailure = null;
        throw new InvalidOperationException("The native window procedure failed.", failure);
    }

    private static int UnsignedLowWord(nint value) => (int)(unchecked((ulong)value) & 0xFFFF);

    private static int UnsignedHighWord(nint value) => (int)((unchecked((ulong)value) >> 16) & 0xFFFF);

    private static uint UnsignedLowWord(nuint value) => (uint)(unchecked((ulong)value) & 0xFFFF);

    private static uint UnsignedHighWord(nuint value) => (uint)((unchecked((ulong)value) >> 16) & 0xFFFF);

    private static int SignedLowWord(nint value) => unchecked((short)(unchecked((ulong)value) & 0xFFFF));

    private static int SignedHighWord(nint value) => unchecked((short)((unchecked((ulong)value) >> 16) & 0xFFFF));

    private static int SignedHighWord(nuint value) => unchecked((short)((unchecked((ulong)value) >> 16) & 0xFFFF));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage
    {
        private readonly nint _window;
        internal readonly uint Message;
        private readonly nuint _wParam;
        private readonly nint _lParam;
        private readonly uint _time;
        private readonly NativePoint _point;
        private readonly uint _private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTrackingRequest
    {
        internal uint Size;
        internal uint Flags;
        internal nint Window;
        internal uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        private nint _deviceContext;
        private int _erase;
        private NativeRect _paint;
        private int _restore;
        private int _incrementalUpdate;
        private int _reserved0;
        private int _reserved1;
        private int _reserved2;
        private int _reserved3;
        private int _reserved4;
        private int _reserved5;
        private int _reserved6;
        private int _reserved7;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectEx(
        ref NativeRect rectangle,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool hasMenu,
        uint extendedStyle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(in WindowClassEx windowClass);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref MouseTrackingRequest tracking);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint window, in PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint window);
}
