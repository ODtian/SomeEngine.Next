using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Vulkan.Tests;

internal sealed class VulkanTestWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private nint _handle;

    internal VulkanTestWindow(int width = 160, int height = 120)
    {
        _handle = CreateWindowExW(
            0,
            "STATIC",
            "SomeEngine Vulkan Test",
            WsPopup,
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
        ResizeClient(width, height);
    }

    internal nint Handle => _handle != 0
        ? _handle
        : throw new ObjectDisposedException(nameof(VulkanTestWindow));

    internal bool IsMinimized => IsIconic(Handle);

    internal void ResizeClient(int width, int height)
    {
        if (!GetClientRect(Handle, out Rectangle client) || !GetWindowRect(Handle, out Rectangle window))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowPos(Handle, 0, 0, 0,
                width + window.Right - window.Left - client.Right,
                height + window.Bottom - window.Top - client.Bottom, 0x0016))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void Minimize() => _ = ShowWindow(Handle, 6);

    internal void Restore()
    {
        _ = ShowWindow(Handle, 9);
        _ = ShowWindow(Handle, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

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
