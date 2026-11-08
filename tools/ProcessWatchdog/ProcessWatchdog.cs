using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Meziantou.Framework.Win32;

[assembly: SupportedOSPlatform("windows8.0")]

namespace SomeEngine.Tools;

public static class ProcessWatchdog
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--execute"])
        {
            ProcessArguments command = JsonSerializer.Deserialize<ProcessArguments>((await Console.In.ReadLineAsync())!)!;
            using Process child = Process.Start(CreateStartInfo(command.Values))!;
            if (command.IdFile is { } idFile)
            {
                await File.WriteAllTextAsync(idFile + ".tmp", child.Id.ToString());
                File.Move(idFile + ".tmp", idFile);
            }
            Task output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            Task errors = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            await child.WaitForExitAsync();
            await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(1000));
            return child.ExitCode;
        }

        int memoryMb = 4096, seconds = 300, idleSeconds = 30, first = 0;
        string? processIdFile = null;
        while (args[first] != "--")
        {
            string option = args[first++];
            string value = args[first++];
            switch (option)
            {
                case "--memory-mb": memoryMb = int.Parse(value); break;
                case "--timeout": seconds = int.Parse(value); break;
                case "--idle-timeout": idleSeconds = int.Parse(value); break;
                case "--process-id-file": processIdFile = value; break;
                default: throw new ArgumentException($"Unknown watchdog option '{option}'.");
            }
        }
        string[] arguments = args[(first + 1)..];
        ulong memoryBytes = checked((ulong)memoryMb * 1024 * 1024);
        MemoryStatus systemMemory = ReadMemory();
        ulong reserve = Math.Min(8UL * 1024 * 1024 * 1024, systemMemory.TotalRam / 8);
        if (systemMemory.AvailableRam < reserve || systemMemory.AvailableCommit < reserve)
        {
            await Console.Error.WriteLineAsync("Watchdog kept the system memory reserve; command startup stopped.");
            return 125;
        }
        string jobName = $"SomeEngine.Watchdog.{Guid.NewGuid():N}";
        using var processes = new JobObject(jobName);
        processes.SetLimits(new JobObjectLimits
        {
            Flags = JobObjectLimitFlags.KillOnJobClose,
            JobMemoryLimit = checked((nuint)memoryBytes),
        });
        using var memoryEvents = new JobMemoryEvents(jobName);

        var start = CreateStartInfo(["dotnet", typeof(ProcessWatchdog).Assembly.Location, "--execute"]);
        start.RedirectStandardInput = true;
        using Process process = Process.Start(start)!;
        int result = 1;
        long outputBytes = 0;
        Task outputCopy = Forward(process.StandardOutput.BaseStream, Console.OpenStandardOutput());
        Task errorCopy = Forward(process.StandardError.BaseStream, Console.OpenStandardError());
        var elapsed = Stopwatch.StartNew();
        long progressAt = 0, previousOutput = 0;
        TimeSpan previousCpu = default;
        try
        {
            // The child waits on stdin until the kernel has installed the whole-tree limits.
            processes.AssignProcess(process);
            await Console.Error.WriteLineAsync($"Watchdog: memory={memoryMb} MiB, timeout={seconds}s, idle={idleSeconds}s, reserve={reserve / 1024 / 1024} MiB.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new ProcessArguments(arguments, processIdFile)));
            process.StandardInput.Close();
            Task exit = process.WaitForExitAsync();
            while (!exit.IsCompleted)
            {
                await Task.WhenAny(exit, Task.Delay(250));
                bool memoryLimitReached = memoryEvents.LimitReached();
                if (exit.IsCompleted && !memoryLimitReached) break;
                ulong peak = (ulong)processes.GetMemoryAccountingInformation().PeakJobMemoryUsed;
                systemMemory = ReadMemory();
                var cpu = processes.GetBasicAccountingInformation();
                TimeSpan cpuTime = cpu.TotalUserTime + cpu.TotalKernelTime;
                long bytes = Interlocked.Read(ref outputBytes);
                if (cpuTime != previousCpu || bytes != previousOutput)
                    progressAt = elapsed.ElapsedMilliseconds;
                previousCpu = cpuTime;
                previousOutput = bytes;

                string? reason = null;
                if (memoryLimitReached || peak >= memoryBytes * 15 / 16)
                {
                    result = 125;
                    reason = memoryLimitReached ? "Windows rejected a process-tree memory allocation at the configured limit"
                        : $"process-tree memory reached {peak / 1024 / 1024} MiB";
                }
                else if (systemMemory.AvailableRam < reserve || systemMemory.AvailableCommit < reserve)
                {
                    result = 125;
                    reason = $"system memory reserve reached; free RAM={systemMemory.AvailableRam / 1024 / 1024} MiB, free commit={systemMemory.AvailableCommit / 1024 / 1024} MiB";
                }
                else if (elapsed.Elapsed.TotalSeconds >= seconds)
                {
                    result = 124;
                    reason = $"elapsed time reached {seconds}s";
                }
                else if (elapsed.ElapsedMilliseconds - progressAt >= idleSeconds * 1000L)
                {
                    result = 126;
                    reason = $"CPU and output were idle for {idleSeconds}s";
                }
                if (reason is not null)
                {
                    await Console.Error.WriteLineAsync($"Watchdog stopped the process tree: {reason}.");
                    processes.Terminate(result);
                    await exit;
                    break;
                }
            }
            if (exit.IsCompletedSuccessfully && result == 1)
                result = memoryEvents.LimitReached() ? 125 : process.ExitCode;
            await Console.Error.WriteLineAsync($"Watchdog finished: exit={result}, peak={processes.GetMemoryAccountingInformation().PeakJobMemoryUsed / 1024 / 1024} MiB, elapsed={elapsed.Elapsed.TotalSeconds:F2}s.");
            return result;
        }
        finally
        {
            // Closing or killing the supervisor also closes this job's sole handle.
            process.StandardInput.Close();
            processes.Terminate(result);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputCopy, errorCopy);
        }

        async Task Forward(Stream source, Stream destination)
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer)) != 0)
            {
                Interlocked.Add(ref outputBytes, count);
                await destination.WriteAsync(buffer.AsMemory(0, count));
                await destination.FlushAsync();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string[] command)
    {
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in command.AsSpan(1)) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        start.Environment["SOMEENGINE_PROCESS_WATCHDOG"] = "1";
        return start;
    }

    private sealed record ProcessArguments(string[] Values, string? IdFile);

    private static MemoryStatus ReadMemory()
    {
        var value = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalRam, AvailableRam, TotalCommit, AvailableCommit;
        internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
