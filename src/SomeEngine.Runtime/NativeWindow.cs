using SomeEngine.Graphics;

namespace SomeEngine.Runtime;

internal abstract class NativeWindow : IDisposable
{
    internal static NativeWindow Create(string title, int width, int height, bool visible) =>
        OperatingSystem.IsWindows() ? new Win32Window(title, width, height, visible) :
        OperatingSystem.IsLinux() ? new XlibWindow(title, width, height, visible) :
        throw new PlatformNotSupportedException("Native windows require Windows or Linux.");

    internal abstract NativeWindowType Type { get; }
    internal nint Handle { get; private protected set; }
    internal nint DisplayHandle { get; private protected set; }
    internal long VisibleTimestamp { get; private protected set; }
    internal int ClientWidth { get; private protected set; }
    internal int ClientHeight { get; private protected set; }
    internal float DpiScale { get; private protected set; } = 1.0f;
    internal bool HasFocus { get; private protected set; }
    internal bool IsMinimized { get; private protected set; }
    internal abstract bool CloseRequested { get; }

    internal abstract void SetClientSize(int width, int height);
    internal abstract bool PumpMessages();
    internal abstract bool TryReadEvent(out NativeWindowEvent windowEvent);
    internal abstract void RequestClose();
    internal abstract void WaitForEvents(TimeSpan timeout);
    internal abstract void WaitForEvents(WaitHandle completion, TimeSpan timeout);
    public abstract void Dispose();
}
