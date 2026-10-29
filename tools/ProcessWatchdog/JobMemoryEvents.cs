using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SomeEngine.Tools;

internal sealed class JobMemoryEvents : IDisposable
{
    private readonly SafeFileHandle _port;

    internal JobMemoryEvents(string jobName)
    {
        _port = CreateIoCompletionPort(new nint(-1), 0, 0, 1);
        if (_port.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using SafeFileHandle job = OpenJobObject(0x0002, false, jobName);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var association = new CompletionPortAssociation { Port = _port.DangerousGetHandle() };
        if (!SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<CompletionPortAssociation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal bool LimitReached()
    {
        bool reached = false;
        while (GetQueuedCompletionStatus(_port, out uint message, out _, out _, 0))
            reached |= message is 9 or 10; // Process or whole-job memory allocation limit.
        return reached;
    }

    public void Dispose() => _port.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionPortAssociation
    {
        internal nint Key;
        internal nint Port;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateIoCompletionPort(nint file, nint existingPort, nuint key, uint threads);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref CompletionPortAssociation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message,
        out nuint key, out nint process, uint milliseconds);
}
