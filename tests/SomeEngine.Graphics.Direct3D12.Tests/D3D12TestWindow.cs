using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal sealed class D3D12TestWindow : IDisposable
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private nint _handle;

    internal D3D12TestWindow(int width = 160, int height = 120)
    {
        _handle = CreateWindowExW(
            0,
            "STATIC",
            "SomeEngine RHI WARP Test",
            WsOverlappedWindow,
            0,
            0,
            width,
            height,
            0,
            0,
            0,
            0);
        if (_handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "A hidden test window could not be created.");
    }

    internal nint Handle => _handle != 0
        ? _handle
        : throw new ObjectDisposedException(nameof(D3D12TestWindow));

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            _ = DestroyWindow(handle);
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
