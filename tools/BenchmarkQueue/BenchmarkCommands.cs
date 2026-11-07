using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace SomeEngine.Tools;

internal static class BenchmarkCommands
{
    private static readonly string QueueName = Environment.GetEnvironmentVariable("SOMEENGINE_BENCHMARK_QUEUE")
        ?? "SomeEngine.BenchmarkQueue.Resources2";
    private static string MutexName(string name) => OperatingSystem.IsWindows() ? @"Global\" + name : name;

    public static int Main(string[] args)
    {
        if (args is ["--worker"]) return ExecuteQueue();
        if (args is ["--execute"]) return ExecuteCommand();
        if (args is ["--remote-worker"])
        {
            try { return LinuxCommands.Run(); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        CommandKind kind = CommandKind.Default;
        QueueSettings settings = QueueSettings.Read();
        if (settings.CpuCoreCount <= 0 || settings.MemoryMiB <= 0 || settings.OutputMiB is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(settings));
        int timeoutSeconds = settings.TimeoutSeconds;
        string directory = Directory.GetCurrentDirectory();
        string sessionId = Environment.GetEnvironmentVariable("SOMEENGINE_BENCHMARK_SESSION")
            ?? Environment.GetEnvironmentVariable("CODEX_THREAD_ID")
            ?? Environment.GetEnvironmentVariable("CODEX_SESSION_ID")
            ?? directory;
        int executableIndex = 0;
        while (executableIndex < args.Length && args[executableIndex].StartsWith("--"))
        {
            string option = args[executableIndex++];
            if (option == "--") break;
            switch (option)
            {
                case "--cpu": kind = CommandKind.Cpu; break;
                case "--local":
                    settings = settings with { Local = true, RemoteOnly = false };
                    break;
                case "--remote":
                    settings = settings with { Local = false, RemoteOnly = true };
                    break;
                case "--gpu":
                case "--exclusive": kind = CommandKind.Gpu; break;
                case "--session": sessionId = args[executableIndex++]; break;
                case "--timeout":
                    if (executableIndex == args.Length)
                        throw new ArgumentException("--timeout requires a value in seconds.");
                    timeoutSeconds = ParseTimeoutSeconds(args[executableIndex++]);
                    break;
                default: throw new ArgumentException($"Unknown queue option: {option}");
            }
        }
        if (executableIndex == args.Length)
            throw new ArgumentException("Usage: BenchmarkQueue [--cpu|--gpu] [--session id] [--timeout seconds] <executable> [arguments...]");

        string executable = args[executableIndex];
        string[] arguments = args[(executableIndex + 1)..];

        string id = Guid.NewGuid().ToString("N");
        string results = Path.Combine(QueueSettings.DataDirectory, "results");
        if (Directory.Exists(results))
            foreach (var previous in new DirectoryInfo(results).EnumerateDirectories()
                .Where(path => File.Exists(Path.Combine(path.FullName, "result.json")))
                .OrderByDescending(path => path.LastWriteTimeUtc).Skip(8)) previous.Delete(recursive: true);
        string output = Path.Combine(QueueSettings.DataDirectory, "results", id);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
        var command = new QueuedCommand(id, executable, arguments,
            directory, output, environment, sessionId, kind, DateTimeOffset.UtcNow)
        {
            TimeoutSeconds = timeoutSeconds,
            Settings = settings,
        };

        using var submission = new Mutex(false, MutexName(QueueName + ".Submit"));
        Wait(submission);
        try
        {
            using var worker = new Mutex(false, MutexName(QueueName + ".Worker"));
            bool startWorker;
            try { startWorker = worker.WaitOne(0); }
            catch (AbandonedMutexException) { startWorker = true; }
            if (startWorker)
            {
                worker.ReleaseMutex();
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                };
                string application = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
                string workerDirectory = Path.Combine(QueueSettings.DataDirectory, "worker-resources2");
                Directory.CreateDirectory(workerDirectory);
                foreach (string file in Directory.GetFiles(Path.GetDirectoryName(application)!))
                    File.Copy(file, Path.Combine(workerDirectory, Path.GetFileName(file)), overwrite: true);
                start.ArgumentList.Add(Path.Combine(workerDirectory, Path.GetFileName(application)));
                start.ArgumentList.Add("--worker");
                using var process = Process.Start(start)!;
            }

            using var pipe = new NamedPipeClientStream(".", QueueName, PipeDirection.InOut);
            pipe.Connect(10000);
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(command));
            Console.WriteLine(reader.ReadLine());
            return 0;
        }
        finally { submission.ReleaseMutex(); }
    }

    private static int ExecuteQueue()
    {
        using var worker = new Mutex(false, MutexName(QueueName + ".Worker"));
        try { if (!worker.WaitOne(0)) return 0; }
        catch (AbandonedMutexException) { }

        var commands = new CommandQueue();
        var execution = new Thread(commands.Run) { IsBackground = true, Name = "benchmark queue" };
        execution.Start();

        while (true)
        {
            using var pipe = new NamedPipeServerStream(QueueName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
            pipe.WaitForConnection();
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            QueuedCommand command = JsonSerializer.Deserialize<QueuedCommand>(reader.ReadLine()!)!;
            if (command.Kind == CommandKind.Default) command = command with { Kind = CommandKind.Cpu };
            commands.Enqueue(command);
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                command.Id, command.OutputDirectory, command.SessionId, Kind = command.Kind.ToString(),
                TotalCpuCount = Environment.ProcessorCount,
                commands.TotalCoreCount,
                command.SubmittedAtUtc, command.TimeoutSeconds, WorkerProcessId = Environment.ProcessId,
            }));
        }
    }

    internal static void WriteDispatchFailure(QueuedCommand command, Exception error)
    {
        Directory.CreateDirectory(command.OutputDirectory);
        File.WriteAllText(Path.Combine(command.OutputDirectory, "stderr.log"), error.ToString());
        File.WriteAllText(Path.Combine(command.OutputDirectory, "stdout.log"), "");
        WriteResult(command, JsonSerializer.Serialize(new
        {
            command.Id, command.Executable, command.Arguments, command.Directory, command.SessionId,
            Kind = command.Kind.ToString(), Host = command.Settings.RemoteHost, ExitCode = 1,
            FailureStage = "RemoteDispatch", ErrorType = error.GetType().Name,
            command.SubmittedAtUtc, FinishedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    internal static void Execute(QueuedCommand command, CommandCpu commandCpu)
    {
        Directory.CreateDirectory(command.OutputDirectory);
        int exitCode = 1;
        bool timedOut = false;
        LocalGpuDevice[] gpus = [];
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            if (command.Kind == CommandKind.Gpu) gpus = GpuDevices.ReadLocal();
            int timeoutSeconds = ParseTimeoutSeconds(command.TimeoutSeconds.ToString());
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = command.Directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--execute");
            start.Environment.Clear();
            foreach (var variable in command.Environment) start.Environment.Add(variable.Key, variable.Value);
            foreach (var variable in command.GetThreadEnvironment()) start.Environment[variable.Key] = variable.Value;
            using var output = File.Create(Path.Combine(command.OutputDirectory, "stdout.log"));
            using var errors = File.Create(Path.Combine(command.OutputDirectory, "stderr.log"));
            using var process = Process.Start(start)!;
            commandCpu.Attach(process);
            process.StandardInput.WriteLine(JsonSerializer.Serialize(command));
            process.StandardInput.Close();
            started = DateTimeOffset.UtcNow;
            using var deadline = new CancellationTokenSource(timeoutSeconds * 1000);
            using var copying = new CancellationTokenSource();
            Task outputCopy = CopyOutput(process.StandardOutput, output, command.Settings.OutputMiB, copying.Token);
            Task errorCopy = CopyOutput(process.StandardError, errors, command.Settings.OutputMiB, copying.Token);
            Task completion = Task.WhenAll(process.WaitForExitAsync(copying.Token), outputCopy, errorCopy);
            try
            {
                completion.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                timedOut = true;
                exitCode = 124;
                try
                {
                    commandCpu.Terminate();
                    if (!process.WaitForExit(5000))
                        throw new TimeoutException("Timed-out process did not exit within 5 seconds of tree termination.");
                }
                finally
                {
                    copying.Cancel();
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                    try { completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                }
            }
        }
        catch (Exception error)
        {
            File.AppendAllText(Path.Combine(command.OutputDirectory, "stderr.log"), error.ToString());
        }
        if (timedOut)
            File.AppendAllText(Path.Combine(command.OutputDirectory, "stderr.log"),
                $"\nBenchmark queue execution timed out after {command.TimeoutSeconds} seconds; process-tree termination was requested.\n");
        var cpu = commandCpu.Complete();
        WriteResult(command, JsonSerializer.Serialize(new
        {
            command.Id, command.Executable, command.Arguments, command.Directory, ExitCode = exitCode,
            command.SessionId, Kind = command.Kind.ToString(), command.SubmittedAtUtc,
            cpu.CpuCount, cpu.CpuSeconds, cpu.PeakCpuCount,
            cpu.CoreCount, cpu.ProcessorMask,
            TotalCpuCount = Environment.ProcessorCount,
            Host = Environment.MachineName,
            CpuModel = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Gpus = gpus,
            ThreadEnvironment = command.GetThreadEnvironment(),
            command.Settings.MemoryMiB, command.Settings.OutputMiB,
            DispatchReason = command.DispatchReason ?? (command.Settings.Local ? "RequestedLocal" : "RemoteResourcesBusy"),
            command.RemoteError,
            command.TimeoutSeconds, TimedOut = timedOut,
            StartedAtUtc = started, FinishedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    internal static void WriteResult(QueuedCommand command, string? json)
    {
        string file = Path.Combine(command.OutputDirectory, "result.json");
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, file);
    }

    private static async Task CopyOutput(StreamReader input, Stream output, int limitMiB, CancellationToken cancellation)
    {
        char[] buffer = new char[4096];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellation)) > 0)
            RemoteCommands.WriteOutput(output, new string(buffer, 0, length), limitMiB);
    }

    private static int ExecuteCommand()
    {
        QueuedCommand command = JsonSerializer.Deserialize<QueuedCommand>(Console.ReadLine()!)!;
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        Task errors = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        Task.WhenAll(process.WaitForExitAsync(), output, errors).GetAwaiter().GetResult();
        return process.ExitCode;
    }

    internal static int ParseTimeoutSeconds(string value)
    {
        if (!int.TryParse(value, out int seconds) || seconds <= 0 || seconds > int.MaxValue / 1000)
            throw new ArgumentException($"--timeout must be a whole number of seconds between 1 and {int.MaxValue / 1000}.");
        return seconds;
    }

    private static void Wait(Mutex mutex)
    {
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
    }

}
