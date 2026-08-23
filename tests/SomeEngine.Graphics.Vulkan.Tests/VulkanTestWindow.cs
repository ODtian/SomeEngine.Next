using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Vulkan.Tests;

internal sealed class VulkanTestWindow : IDisposable
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private nint _handle;

    internal VulkanTestWindow(int width = 160, int height = 120)
    {
        _handle = CreateWindowExW(
            0,
            "STATIC",
            "SomeEngine Vulkan Test",
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
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal nint Handle => _handle != 0
        ? _handle
        : throw new ObjectDisposedException(nameof(VulkanTestWindow));

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
