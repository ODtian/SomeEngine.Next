using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace SomeEngine.Tools;

internal sealed record SourceFile(string Path, long Length, long ModifiedTicks);
internal sealed record RemoteRequest(QueuedCommand Command, string SourceDirectory, string WorkingDirectory, SourceFile[] Files);
internal sealed record RemoteReply(bool Accepted, string Reason, string[] Files, int[] Processors, string CpuModel, string? GpuModel)
{
    public bool ResourcesBusy { get; init; }
}
internal sealed record RemoteMessage(string? Stream, string? Text, JsonElement? Result);
internal sealed record RemoteReservation(Process Connection, Task<string> Errors, RemoteRequest Request, RemoteReply Reply);

internal static class RemoteCommands
{
    public static RemoteReservation? TryReserve(QueuedCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Settings.RemoteHost))
            throw new InvalidOperationException("Configure the remote benchmark host before submitting tasks.");
        var git = ProcessCommands.StartInfo("git", "rev-parse", "--show-toplevel");
        git.WorkingDirectory = command.Directory;
        var root = ProcessCommands.Run(git);
        string directory = root.ExitCode == 0 ? Path.GetFullPath(root.Output.Trim()) : command.Directory;
        string[] paths;
        if (root.ExitCode == 0)
        {
            var listing = ProcessCommands.StartInfo("git", "ls-files", "--cached", "--others", "--exclude-standard", "-z");
            listing.WorkingDirectory = directory;
            var listed = ProcessCommands.Run(listing);
            if (listed.ExitCode != 0) throw new IOException(listed.Errors);
            paths = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        else
            paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')).ToArray();
        var files = paths
            .Distinct().Select(path => (Path: path, File: new FileInfo(Path.Combine(directory, path))))
            .Where(entry => entry.File.Exists)
            .Select(entry => new SourceFile(entry.Path, entry.File.Length, entry.File.LastWriteTimeUtc.Ticks)).ToArray();
        var request = new RemoteRequest(command, directory, Path.GetRelativePath(directory, command.Directory).Replace('\\', '/'), files);
        Process? connection = null;
        try
        {
            var start = ProcessCommands.StartInfo("ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
                command.Settings.RemoteHost, Quote(command.Settings.RemoteDirectory + "/queue") + " --remote-worker");
            start.RedirectStandardInput = true;
            connection = Process.Start(start)!;
            Task<string> errors = connection.StandardError.ReadToEndAsync();
            connection.StandardInput.WriteLine(JsonSerializer.Serialize(request));
            connection.StandardInput.Flush();
            string? line = connection.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            if (line is not null)
            {
                var reply = JsonSerializer.Deserialize<RemoteReply>(line)!;
                if (reply.Accepted) return new(connection, errors, request, reply);
                if (reply.ResourcesBusy)
                {
                    connection.StandardInput.Close();
                    connection.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    connection.Dispose();
                    return null;
                }
                throw new IOException(reply.Reason);
            }
            throw new IOException("Remote executor closed the connection: " + errors.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        catch
        {
            if (connection is not null)
            {
                if (!connection.HasExited) connection.Kill(entireProcessTree: true);
                connection.Dispose();
            }
            throw;
        }
    }

    public static void Execute(QueuedCommand command, RemoteReservation reservation)
    {
        Directory.CreateDirectory(command.OutputDirectory);
        using var connection = reservation.Connection;
        using var output = File.Create(Path.Combine(command.OutputDirectory, "stdout.log"));
        using var errors = File.Create(Path.Combine(command.OutputDirectory, "stderr.log"));
        string? resultJson = null;
        try
        {
            if (reservation.Reply.Files.Length > 0)
            {
                using var archive = new TarWriter(connection.StandardInput.BaseStream, TarEntryFormat.Pax, leaveOpen: true);
                foreach (string path in reservation.Reply.Files)
                    archive.WriteEntry(Path.Combine(reservation.Request.SourceDirectory, path), path);
            }
            connection.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds + 300));
            while (connection.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() is { } line)
            {
                var message = JsonSerializer.Deserialize<RemoteMessage>(line)!;
                if (message.Result is { } result)
                {
                    resultJson = result.GetRawText();
                }
                else WriteOutput(message.Stream == "stdout" ? output : errors, message.Text!, command.Settings.OutputMiB);
            }
            connection.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            if (resultJson is null) throw new IOException(reservation.Errors.GetAwaiter().GetResult());
        }
        catch (Exception error)
        {
            if (!connection.HasExited) connection.Kill(entireProcessTree: true);
            WriteOutput(errors, error.ToString(), command.Settings.OutputMiB);
            if (resultJson is null)
                resultJson = JsonSerializer.Serialize(new
                {
                    command.Id, ExitCode = 1, Host = command.Settings.RemoteHost,
                    command.SessionId, command.SubmittedAtUtc, FinishedAtUtc = DateTimeOffset.UtcNow,
                });
        }
        output.Dispose();
        errors.Dispose();
        BenchmarkCommands.WriteResult(command, resultJson);
    }

    internal static void WriteOutput(Stream output, string text, int limitMiB)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int length = (int)Math.Min(bytes.Length, Math.Max(0, (long)limitMiB * 1024 * 1024 - output.Length));
        output.Write(bytes, 0, length);
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
