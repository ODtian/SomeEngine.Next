using System.Diagnostics;

namespace SomeEngine.Tools;

internal static class ProcessCommands
{
    public static ProcessStartInfo StartInfo(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static (int ExitCode, string Output, string Errors) Run(ProcessStartInfo start, int seconds = 30)
    {
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            Task.WhenAll(process.WaitForExitAsync(), output, errors)
                .WaitAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
            return (process.ExitCode, output.Result, errors.Result);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
