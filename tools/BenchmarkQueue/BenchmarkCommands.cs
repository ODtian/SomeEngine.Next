using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace SomeEngine.Tools;

internal static class BenchmarkCommands
{
    private static readonly string QueueName = Environment.GetEnvironmentVariable("SOMEENGINE_BENCHMARK_QUEUE")
        ?? "SomeEngine.BenchmarkQueue";
    private static string MutexName(string name) => OperatingSystem.IsWindows() ? @"Global\" + name : name;

    public static int Main(string[] args)
    {
        if (args is ["--worker"]) return ExecuteQueue();
        int timeoutSeconds = QueuedCommand.DefaultTimeoutSeconds;
        int executableIndex = 0;
        while (executableIndex < args.Length && args[executableIndex].StartsWith("--"))
        {
            string option = args[executableIndex++];
            if (option == "--") break;
            switch (option)
            {
                case "--timeout":
                    if (executableIndex == args.Length)
                        throw new ArgumentException("--timeout requires a value in seconds.");
                    timeoutSeconds = ParseTimeoutSeconds(args[executableIndex++]);
                    break;
                default: throw new ArgumentException($"Unknown queue option: {option}");
            }
        }
        if (executableIndex == args.Length)
            throw new ArgumentException("Usage: BenchmarkQueue [--timeout seconds] <executable> [arguments...]");

        string id = Guid.NewGuid().ToString("N");
        string directory = Directory.GetCurrentDirectory();
        string output = Path.Combine(directory, "artifacts", "benchmark-queue", id);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
        var command = new QueuedCommand(id, args[executableIndex], args[(executableIndex + 1)..],
            directory, output, environment) { TimeoutSeconds = timeoutSeconds };

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
                start.ArgumentList.Add(typeof(BenchmarkCommands).Assembly.Location);
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

        using var commands = new BlockingCollection<QueuedCommand>(new ConcurrentQueue<QueuedCommand>());
        var execution = new Thread(() =>
        {
            foreach (QueuedCommand command in commands.GetConsumingEnumerable()) Execute(command);
        }) { IsBackground = true, Name = "benchmark queue" };
        execution.Start();

        while (true)
        {
            using var pipe = new NamedPipeServerStream(QueueName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
            pipe.WaitForConnection();
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            QueuedCommand command = JsonSerializer.Deserialize<QueuedCommand>(reader.ReadLine()!)!;
            commands.Add(command);
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                command.Id, command.OutputDirectory, command.TimeoutSeconds, WorkerProcessId = Environment.ProcessId,
            }));
        }
    }

    private static void Execute(QueuedCommand command)
    {
        Directory.CreateDirectory(command.OutputDirectory);
        int exitCode = 1;
        bool timedOut = false;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            int timeoutSeconds = ParseTimeoutSeconds(command.TimeoutSeconds.ToString());
            var start = new ProcessStartInfo(command.Executable)
            {
                WorkingDirectory = command.Directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var variable in command.Environment) start.Environment.Add(variable.Key, variable.Value);
            using var output = File.Create(Path.Combine(command.OutputDirectory, "stdout.log"));
            using var errors = File.Create(Path.Combine(command.OutputDirectory, "stderr.log"));
            using var process = Process.Start(start)!;
            started = DateTimeOffset.UtcNow;
            using var deadline = new CancellationTokenSource(timeoutSeconds * 1000);
            using var copying = new CancellationTokenSource();
            Task outputCopy = process.StandardOutput.BaseStream.CopyToAsync(output, copying.Token);
            Task errorCopy = process.StandardError.BaseStream.CopyToAsync(errors, copying.Token);
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
                    // Keep the original Process handle: descendants may still hold redirected pipes
                    // after the root process exits, so do not skip tree termination when HasExited.
                    process.Kill(entireProcessTree: true);
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
        File.WriteAllText(Path.Combine(command.OutputDirectory, "result.json"), JsonSerializer.Serialize(new
        {
            command.Id, command.Executable, command.Arguments, command.Directory, ExitCode = exitCode,
            command.TimeoutSeconds, TimedOut = timedOut,
            StartedAtUtc = started, FinishedAtUtc = DateTimeOffset.UtcNow,
        }));
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

    private sealed record QueuedCommand(string Id, string Executable, string[] Arguments, string Directory,
        string OutputDirectory, Dictionary<string, string> Environment)
    {
        public const int DefaultTimeoutSeconds = 60;
        public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    }
}
