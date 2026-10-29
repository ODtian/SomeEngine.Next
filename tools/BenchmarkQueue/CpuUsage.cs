using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Tools;

internal sealed class CpuUsage
{
    private long idleTime;
    private long processorTime;

    public double AvailableCpuCount { get; private set; } = Environment.ProcessorCount;

    public void Begin()
    {
        if (OperatingSystem.IsWindows())
        {
            ReadTimes(out idleTime, out long kernelTime, out long userTime);
            processorTime = kernelTime + userTime;
        }
    }

    public void Sample()
    {
        if (!OperatingSystem.IsWindows()) return;
        ReadTimes(out long idle, out long kernel, out long user);
        long total = kernel + user;
        AvailableCpuCount = Environment.ProcessorCount * (double)(idle - idleTime) / (total - processorTime);
        idleTime = idle;
        processorTime = total;
    }

    private static void ReadTimes(out long idle, out long kernel, out long user)
    {
        if (!GetSystemTimes(out idle, out kernel, out user)) throw new Win32Exception();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
