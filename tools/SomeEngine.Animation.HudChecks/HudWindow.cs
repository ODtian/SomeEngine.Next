using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Animation.HudChecks;

internal sealed class HudWindow : IDisposable
{
    internal nint Handle { get; } = CreateWindowExW(0, "STATIC", "SomeEngine typed animation HUD",
        0x10CF0000, 80, 80, 1296, 759, 0, 0, 0, 0);

    internal HudWindow()
    {
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void Pump()
    {
        while (PeekMessageW(out Message message, 0, 0, 0, 1))
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }
    }

    public void Dispose() => DestroyWindow(Handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in Message message);
}
