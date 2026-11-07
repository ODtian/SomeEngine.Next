using System.Diagnostics;
using Meziantou.Framework.Win32;

namespace SomeEngine.Tools;

internal sealed class CommandCpu : IDisposable
{
    private readonly object sync = new();
    private readonly JobObject? processes = OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600) ? new JobObject() : null;
    private Process? process;
    private long started;
    private long sampled;
    private TimeSpan cpuTime;
    private bool finished;
    private double cpuCount;
    private double peakCpuCount;
    private ulong processorMask;
    private int coreCount;
    private readonly int memoryMiB;

    public CommandCpu(int memoryMiB = 8192) => this.memoryMiB = memoryMiB;

    public ulong ProcessorMask { get { lock (sync) return processorMask; } }

    public void SetProcessors(ulong mask, int count)
    {
        lock (sync)
        {
            if (finished || mask == processorMask) return;
            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
                processes!.SetLimits(new JobObjectLimits
                {
                    Affinity = (nuint)mask,
                    JobMemoryLimit = (nuint)((long)memoryMiB * 1024 * 1024),
                    Flags = JobObjectLimitFlags.KillOnJobClose,
                    ActiveProcessLimit = 256,
                });
            else if (OperatingSystem.IsLinux() && process is not null)
                process.ProcessorAffinity = (nint)mask;
            processorMask = mask;
            coreCount = count;
        }
    }

    public void Attach(Process commandProcess)
    {
        lock (sync)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600)) processes!.AssignProcess(commandProcess);
            else
            {
                process = Process.GetProcessById(commandProcess.Id);
                if (OperatingSystem.IsLinux()) process.ProcessorAffinity = (nint)processorMask;
            }
            started = sampled = Stopwatch.GetTimestamp();
        }
    }

    public void Terminate()
    {
        lock (sync)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600)) processes!.Terminate(124);
            else process!.Kill(entireProcessTree: true);
        }
    }

    public void Sample()
    {
        lock (sync)
        {
            if (started == 0 || finished) return;
            long now = Stopwatch.GetTimestamp();
            TimeSpan total = ReadCpuTime();
            cpuCount = Math.Clamp((total - cpuTime).TotalSeconds /
                Stopwatch.GetElapsedTime(sampled, now).TotalSeconds, 0, Environment.ProcessorCount);
            peakCpuCount = Math.Max(peakCpuCount, cpuCount);
            cpuTime = total;
            sampled = now;
        }
    }

    public (double CpuCount, double CpuSeconds, double PeakCpuCount, int CoreCount, ulong ProcessorMask) Complete()
    {
        lock (sync)
        {
            finished = true;
            TimeSpan total = started == 0 ? TimeSpan.Zero : ReadCpuTime();
            double average = started == 0 ? 0 : total.TotalSeconds / Stopwatch.GetElapsedTime(started).TotalSeconds;
            average = Math.Min(average, Environment.ProcessorCount);
            return (average, total.TotalSeconds, Math.Max(peakCpuCount, average), coreCount, processorMask);
        }
    }

    private TimeSpan ReadCpuTime()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
        {
            var accounting = processes!.GetBasicAccountingInformation();
            return accounting.TotalUserTime + accounting.TotalKernelTime;
        }
        return process!.HasExited ? cpuTime : process.TotalProcessorTime;
    }

    public void Dispose()
    {
        lock (sync)
        {
            finished = true;
            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600)) processes!.Dispose();
            process?.Dispose();
        }
    }
}
