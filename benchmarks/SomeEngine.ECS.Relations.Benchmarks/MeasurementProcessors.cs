using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace SomeEngine.ECS.Relations.Benchmarks;

public enum MeasurementMachine
{
    Normal,
    Fixed
}

internal static partial class MeasurementProcessors
{
    internal static MeasurementProcessorData Configure(MeasurementMachine machine)
    {
        using Process process = Process.GetCurrentProcess();
        process.PriorityClass = machine == MeasurementMachine.Fixed ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
        nuint processMask = (nuint)process.ProcessorAffinity;
        nuint threadMask = machine == MeasurementMachine.Fixed ? (nuint)1 << 2 : processMask;
        nuint previous = SetThreadAffinityMask(GetCurrentThread(), threadMask);
        if (previous == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(machine.ToString(), (ulong)processMask, (ulong)threadMask, (ulong)previous,
            process.PriorityClass.ToString(), Thread.CurrentThread.Priority.ToString(), GetCurrentProcessorNumber());
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessorNumber();
}
