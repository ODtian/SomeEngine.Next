using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed class BenchmarkWindow : IDisposable
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private nint _handle;

    internal BenchmarkWindow()
    {
        _handle = CreateWindowExW(
            0,
            "STATIC",
            "SomeEngine Graphics Benchmark",
            WsOverlappedWindow,
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            0,
            0,
            0,
            0);
        if (_handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The benchmark window could not be created.");
    }

    internal nint Handle => _handle != 0
        ? _handle
        : throw new ObjectDisposedException(nameof(BenchmarkWindow));

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
