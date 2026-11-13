using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json;

namespace SomeEngine.Tools;

internal static class LinuxCommands
{
    public static int Run()
    {
        var request = JsonSerializer.Deserialize<RemoteRequest>(Console.ReadLine()!)!;
        QueuedCommand command = request.Command;
        string root = AppContext.BaseDirectory;
        using var directory = LinuxWorkDirectory.Acquire(root, command.Settings, command.Kind);
        if (directory is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new RemoteReply(false, "Remote resources are busy.", [], [], "", null)
            { ResourcesBusy = true }));
            return 0;
        }
        string cpu = File.ReadLines("/proc/cpuinfo").First(line => line.StartsWith("model name")).Split(':', 2)[1].Trim();
        string source = Path.Combine(directory.Path, "source");
        string manifest = Path.Combine(directory.Path, "source.json");
        SourceFile[] previous = File.Exists(manifest)
            ? JsonSerializer.Deserialize<SourceFile[]>(File.ReadAllText(manifest))! : [];
        Dictionary<string, SourceFile> old = previous.ToDictionary(file => file.Path);
        string[] changed = request.Files.Where(file => !old.TryGetValue(file.Path, out var saved)
            || saved.Length != file.Length || saved.ModifiedTicks != file.ModifiedTicks
            || !File.Exists(Path.Combine(source, file.Path))).Select(file => file.Path).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new RemoteReply(true, "", changed, directory.Processors, cpu, directory.Gpu?.Name)));
        Console.Out.Flush();

        var sent = new HashSet<string>(request.Files.Select(file => file.Path));
        foreach (SourceFile file in previous)
            if (!sent.Contains(file.Path)) File.Delete(Path.Combine(source, file.Path));
        Directory.CreateDirectory(source);
        if (changed.Length > 0) TarFile.ExtractToDirectory(Console.OpenStandardInput(), source, overwriteFiles: true);
        File.WriteAllText(manifest, JsonSerializer.Serialize(request.Files));
        string temporary = Path.Combine(directory.Path, "temporary");
        Directory.CreateDirectory(temporary);
        string home = Path.Combine(directory.Path, "home"); Directory.CreateDirectory(home);
        string unit = "someengine-benchmark-" + command.Id;
        var start = ProcessCommands.StartInfo("systemd-run", "--user", "--wait", "--pipe", "--quiet", "--unit=" + unit,
            "--property=MemoryMax=" + command.Settings.MemoryMiB + "M", "--property=MemorySwapMax=0",
            "--property=RuntimeMaxSec=" + command.TimeoutSeconds, "--property=TimeoutStopSec=1",
            "--property=KillMode=control-group", "--property=TasksMax=256", "--property=RemainAfterExit=yes",
            "--property=CPUAffinity=" + string.Join(' ', directory.Processors),
            "--working-directory=" + Path.Combine(source, request.WorkingDirectory));
        ConfigureBus(start);
        var environment = command.GetThreadEnvironment();
        foreach (var variable in command.Environment.Where(variable => variable.Key.StartsWith("SOMEENGINE_")))
            environment[variable.Key] = variable.Value;
        if (command.DiagnosticScope is { } scope) environment["SOMEENGINE_BENCHMARK_DIAGNOSTIC_SCOPE"] = scope;
        environment["HOME"] = home; environment["TMPDIR"] = temporary;
        environment["DOTNET_CLI_HOME"] = home;
        environment["NUGET_PACKAGES"] = Path.Combine(directory.Path, "packages");
        string dotnet = Path.Combine(root, "tools", "dotnet");
        if (File.Exists(Path.Combine(dotnet, "dotnet")))
        {
            environment["DOTNET_ROOT"] = dotnet;
            environment["PATH"] = dotnet + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        if (directory.Gpu is { } gpu)
            foreach (var variable in GpuDevices.EnvironmentFor(gpu)) environment[variable.Key] = variable.Value;
        foreach (var variable in environment) start.ArgumentList.Add("--setenv=" + variable.Key + "=" + variable.Value);
        start.ArgumentList.Add("--");
        string Map(string argument)
        {
            string mapped = argument.Replace(request.SourceDirectory, source, StringComparison.OrdinalIgnoreCase)
                .Replace(request.SourceDirectory.Replace('\\', '/'), source, StringComparison.OrdinalIgnoreCase);
            return mapped == argument ? argument : mapped.Replace('\\', '/');
        }
        string executable = command.Executable == "python" ? "python3" : Map(command.Executable);
        if (executable == "dotnet" && File.Exists(Path.Combine(dotnet, "dotnet"))) executable = Path.Combine(dotnet, "dotnet");
        start.ArgumentList.Add(executable);
        foreach (string argument in command.Arguments) start.ArgumentList.Add(Map(argument));
        DateTimeOffset started = DateTimeOffset.UtcNow;
        object outputLock = new();
        int exitCode = 1; string result = "failed"; long? memoryPeak = null; long cpuNanoseconds = 0;
        var sample = new SampleCheck(command.SampleLimit, command.DiagnosticScope);
        try
        {
            using var process = Process.Start(start)!;
            Task stdout = Forward(process.StandardOutput, "stdout", outputLock, sample, unit);
            Task stderr = Forward(process.StandardError, "stderr", outputLock, sample, unit);
            var status = ProcessCommands.StartInfo("systemctl", "--user", "show", unit,
                "-p", "Result", "-p", "SubState", "-p", "ExecMainStatus", "-p", "ControlGroup", "-p", "CPUUsageNSec");
            ConfigureBus(status);
            Dictionary<string, string> fields;
            var elapsed = Stopwatch.StartNew();
            do
            {
                Task.Delay(100).GetAwaiter().GetResult();
                fields = ProcessCommands.Run(status).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
            } while (fields.GetValueOrDefault("SubState") is not ("exited" or "failed")
                && elapsed.Elapsed.TotalSeconds < command.TimeoutSeconds + 5);
            result = fields.GetValueOrDefault("Result", "failed");
            int.TryParse(fields.GetValueOrDefault("ExecMainStatus"), out exitCode);
            string peakFile = "/sys/fs/cgroup" + fields.GetValueOrDefault("ControlGroup") + "/memory.peak";
            if (File.Exists(peakFile)) memoryPeak = long.Parse(File.ReadAllText(peakFile));
            long.TryParse(fields.GetValueOrDefault("CPUUsageNSec"), out cpuNanoseconds);
            var stop = ProcessCommands.StartInfo("systemctl", "--user", "stop", unit); ConfigureBus(stop); ProcessCommands.Run(stop);
            Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (process.ExitCode != 0) exitCode = process.ExitCode;
            if (result == "timeout") exitCode = 124;
            else if (result == "oom-kill") exitCode = 137;
        }
        catch (Exception error)
        {
            lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(new RemoteMessage("stderr", error.ToString(), null)));
        }
        finally
        {
            var stop = ProcessCommands.StartInfo("systemctl", "--user", "stop", unit); ConfigureBus(stop); ProcessCommands.Run(stop);
            var reset = ProcessCommands.StartInfo("systemctl", "--user", "reset-failed", unit); ConfigureBus(reset); ProcessCommands.Run(reset);
            Directory.Delete(temporary, recursive: true);
        }
        if (sample.Failed) exitCode = 1;
        var details = JsonSerializer.SerializeToElement(new
        {
            command.Id, command.Executable, command.Arguments, command.Directory, command.SessionId,
            Kind = command.Kind.ToString(), Host = Environment.MachineName, CpuModel = cpu,
            GpuModel = directory.Gpu?.Name, GpuId = directory.Gpu?.Id,
            GpuFreeMiBAtStart = directory.Gpu?.FreeMiB, GpuUtilizationAtStart = directory.Gpu?.Utilization,
            CoreCount = command.Settings.CpuCoreCount, Processors = directory.Processors,
            command.Settings.MemoryMiB, command.Settings.OutputMiB,
            ThreadEnvironment = command.GetThreadEnvironment(),
            MemoryPeakBytes = memoryPeak, CpuSeconds = cpuNanoseconds / 1e9,
            WorkDirectoryLimitBytes = LinuxWorkDirectory.DirectoryBytes,
            command.TimeoutSeconds, TimedOut = result == "timeout", MemoryLimitExceeded = result == "oom-kill",
            command.SampleLimit, command.DiagnosticScope, sample.FirstSample, FailedSample = sample.Failed ? sample.Sample : null,
            SampleTargetExceeded = sample.Failed, StopReason = sample.Failed ? "SampleTarget" : result,
            ExitCode = exitCode, command.SubmittedAtUtc, StartedAtUtc = started, FinishedAtUtc = DateTimeOffset.UtcNow,
        });
        Console.WriteLine(JsonSerializer.Serialize(new RemoteMessage(null, null, details)));
        return 0;
    }

    private static async Task Forward(StreamReader reader, string stream, object outputLock, SampleCheck sample, string unit)
    {
        char[] buffer = new char[4096]; int length;
        while ((length = await reader.ReadAsync(buffer)) > 0)
        {
            string text = new(buffer, 0, length);
            lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(new RemoteMessage(stream, text, null)));
            if (sample.Observe(text, stream == "stderr"))
            {
                lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(new RemoteMessage("stderr",
                    $"Sample {sample.Sample} failed target {sample.Target}; the process group was terminated.\n", null)));
                var stop = ProcessCommands.StartInfo("systemctl", "--user", "kill", "--signal=KILL", unit);
                ConfigureBus(stop);
                ProcessCommands.Run(stop);
            }
        }
    }

    private static void ConfigureBus(ProcessStartInfo start)
    {
        string userDirectory = "/run/user/" + File.ReadLines("/proc/self/status").First(line => line.StartsWith("Uid:")).Split('\t')[1];
        start.Environment["XDG_RUNTIME_DIR"] = userDirectory;
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + userDirectory + "/bus";
    }
}
