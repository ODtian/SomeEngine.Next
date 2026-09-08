using System.Runtime.InteropServices;

namespace SomeEngine.Diagnostics;

internal static partial class TraceThread
{
    internal static int CurrentNativeId => OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentThreadId();
}
