using System.Diagnostics;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

internal static class DefaultGameCapture
{
    internal sealed record Result(string Command, int ExitCode, double ElapsedSeconds);

    internal static async Task<Result> RunAsync(string application, IReadOnlyList<string> arguments,
        string image, ITestOutputHelper output)
    {
        var start = new ProcessStartInfo(application)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        string command = application + " " + string.Join(" ", arguments.Select(argument => '"' + argument + '"'));
        output.WriteLine(command);
        var elapsed = Stopwatch.StartNew();
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        bool timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        string log = $"{command}\nExitCode: {process.ExitCode}\nElapsedSeconds: {elapsed.Elapsed.TotalSeconds:R}\n{await stdout}\n{await stderr}";
        await File.WriteAllTextAsync(Path.ChangeExtension(image, ".log"), log);
        Assert.False(timedOut, $"DefaultGame exceeded its 60 second capture limit. {log}");
        Assert.True(process.ExitCode == 0, log);
        Assert.True(File.Exists(image), $"DefaultGame must deliver the requested image. {log}");
        return new(command, process.ExitCode, elapsed.Elapsed.TotalSeconds);
    }
}
