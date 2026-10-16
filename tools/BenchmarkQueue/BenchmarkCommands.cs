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
        if (args.Length == 0) throw new ArgumentException("Usage: BenchmarkQueue <executable> [arguments...]");

        string id = Guid.NewGuid().ToString("N");
        string directory = Directory.GetCurrentDirectory();
        string output = Path.Combine(directory, "artifacts", "benchmark-queue", id);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
        var command = new QueuedCommand(id, args[0], args[1..], directory, output, environment);

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
                command.Id, command.OutputDirectory, WorkerProcessId = Environment.ProcessId,
            }));
        }
    }

    private static void Execute(QueuedCommand command)
    {
        Directory.CreateDirectory(command.OutputDirectory);
        int exitCode = 1;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
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
            Task outputCopy = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task errorCopy = process.StandardError.BaseStream.CopyToAsync(errors);
            process.WaitForExit();
            Task.WhenAll(outputCopy, errorCopy).GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        catch (Exception error)
        {
            File.AppendAllText(Path.Combine(command.OutputDirectory, "stderr.log"), error.ToString());
        }
        File.WriteAllText(Path.Combine(command.OutputDirectory, "result.json"), JsonSerializer.Serialize(new
        {
            command.Id, command.Executable, command.Arguments, command.Directory, ExitCode = exitCode,
            StartedAtUtc = started, FinishedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    private static void Wait(Mutex mutex)
    {
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
    }

    private sealed record QueuedCommand(string Id, string Executable, string[] Arguments, string Directory,
        string OutputDirectory, Dictionary<string, string> Environment);
}
