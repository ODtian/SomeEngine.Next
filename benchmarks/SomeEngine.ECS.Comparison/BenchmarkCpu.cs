using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

internal static class BenchmarkCpu
{
    internal static int[] ParticipantProcessors { get; private set; } = [];

    internal static void ConfigureNative(string? workload)
    {
        if (!OperatingSystem.IsLinux() || workload != "job-native-parallel" || ParticipantProcessors.Length != 0) return;
        string allowed = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:")).Split(':')[1].Trim();
        int[] processors = allowed.Split(',').SelectMany(part =>
        {
            int[] range = part.Split('-').Select(int.Parse).ToArray();
            return Enumerable.Range(range[0], range[^1] - range[0] + 1);
        }).ToArray();
        ParticipantProcessors = processors.GroupBy(cpu => (
            File.ReadAllText($"/sys/devices/system/cpu/cpu{cpu}/topology/physical_package_id").Trim(),
            File.ReadAllText($"/sys/devices/system/cpu/cpu{cpu}/topology/core_id").Trim()))
            .Select(core => core.Min()).Order().ToArray();
        byte[] mask = new byte[128];
        foreach (int cpu in ParticipantProcessors) mask[cpu >> 3] |= (byte)(1 << (cpu & 7));
        foreach (string thread in Directory.EnumerateDirectories("/proc/self/task"))
            if (SetLinuxThreadAffinity(int.Parse(Path.GetFileName(thread)), (nuint)mask.Length, mask) != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static void BindCurrentThread()
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetCurrentProcess();
            nuint processors = (nuint)process.ProcessorAffinity;
            SetThreadAffinityMask(GetCurrentThread(), processors & (~processors + 1));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint processors);

    [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static extern int SetLinuxThreadAffinity(int thread, nuint bytes, byte[] processors);
}
