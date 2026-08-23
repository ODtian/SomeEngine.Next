using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SomeEngine.Graphics.Benchmarks;

internal readonly record struct SchedulingResult(bool Established, string Reason);

internal static class BenchmarkEnvironment
{
    internal static SchedulingResult EstablishScheduling(BenchmarkProfile profile)
    {
        if (!OperatingSystem.IsWindows())
            return new SchedulingResult(false, "The D3D12 benchmark requires Windows.");
        try
        {
            using Process process = Process.GetCurrentProcess();
            long available = process.ProcessorAffinity.ToInt64();
            if (available == 0)
                return new SchedulingResult(false, "The process has no available CPU affinity bits.");
            if (profile == BenchmarkProfile.RepresentativeCpuFrame)
            {
                process.PriorityClass = ProcessPriorityClass.High;
                return new SchedulingResult(
                    true,
                    $"Retained multicore affinity 0x{available:X} at High priority for representative parallel recording.");
            }
            long selected = SelectHighestAffinityBit(available);
            process.ProcessorAffinity = new nint(selected);
            process.PriorityClass = ProcessPriorityClass.High;
            return new SchedulingResult(true, $"Pinned to affinity 0x{selected:X} at High priority.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return new SchedulingResult(
                profile != BenchmarkProfile.VendorCertification,
                $"CPU affinity/priority policy could not be established: {exception.Message}");
        }
    }

    internal static long SelectHighestAffinityBit(long available)
    {
        ulong bits = unchecked((ulong)available);
        if (bits == 0)
            throw new ArgumentOutOfRangeException(nameof(available));
        return unchecked((long)(1UL << BitOperations.Log2(bits)));
    }

    internal static RuntimeEnvironment Capture(
        in WorkerConfiguration configuration,
        in AdapterInfo adapter,
        bool validationEnabled,
        bool dredEnabled,
        string toolchain)
    {
        using Process process = Process.GetCurrentProcess();
        string executable = ResolveExecutablePath();
        return new RuntimeEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadProcessorName(),
            Environment.ProcessId,
            configuration.ProcessIndex,
            ReadAffinity(process),
            process.PriorityClass.ToString(),
            ReadPowerMode(),
            adapter.Name,
            adapter.VendorId,
            adapter.DeviceId,
            adapter.Id.Low,
            adapter.Id.High,
            adapter.DriverVersion,
            adapter.HardwareAccelerated,
            619,
            validationEnabled,
            dredEnabled,
            CaptureToolLoaded(process),
            new BuildIdentity(
                executable,
                File.Exists(executable) ? Sha256File(executable) : string.Empty,
                BuildPayloadSha256(AppContext.BaseDirectory),
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                ReadGit("rev-parse HEAD") ??
                    Environment.GetEnvironmentVariable("GITHUB_SHA") ??
                    Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION") ??
                    "unknown",
                !string.IsNullOrWhiteSpace(ReadGit("status --porcelain")),
                toolchain,
                "public-end-return"));
    }

    internal static RuntimeEnvironment Unavailable(int processIndex, string toolchain = "unavailable") => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        ReadProcessorName(),
        0,
        processIndex,
        0,
        "unavailable",
        ReadPowerMode(),
        string.Empty,
        0,
        0,
        0,
        0,
        string.Empty,
        false,
        619,
        false,
        false,
        false,
        new BuildIdentity(string.Empty, string.Empty, string.Empty, "unknown", "unknown", "unknown", true, toolchain));

    internal static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string Sha256Bytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal static string BuildPayloadSha256(string directory)
    {
        string[] extensions = [".dll", ".exe", ".json"];
        string[] files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
            .ToArray();
        var manifest = new StringBuilder();
        foreach (string path in files)
        {
            manifest.Append(Path.GetRelativePath(directory, path).Replace('\\', '/'))
                .Append('\n')
                .Append(Sha256File(path))
                .Append('\n');
        }
        return Sha256Bytes(Encoding.UTF8.GetBytes(manifest.ToString()));
    }

    private static string ReadProcessorName()
    {
        if (OperatingSystem.IsWindows())
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null);
            if (value is string name && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Trim() is { Length: > 0 } fallback
            ? fallback
            : "unavailable";
    }

    private static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }
        string managedEntry = Path.Combine(
            AppContext.BaseDirectory,
            "SomeEngine.Graphics.Benchmarks.dll");
        if (File.Exists(managedEntry))
            return Path.GetFullPath(managedEntry);
        if (!string.IsNullOrWhiteSpace(processPath))
            return Path.GetFullPath(processPath);
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "SomeEngine.Graphics.Benchmarks.exe"));
    }

    private static long ReadAffinity(Process process)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return 0;
        return process.ProcessorAffinity.ToInt64();
    }

    private static bool CaptureToolLoaded(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                string name = module.ModuleName;
                if (name.Contains("WinPixGpuCapturer", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("renderdoc", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("nsight", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }
        return false;
    }

    private static string ReadPowerMode()
    {
        if (!OperatingSystem.IsWindows())
            return "unavailable";
        string? output = RunProcess("powercfg.exe", "/getactivescheme", Directory.GetCurrentDirectory());
        if (output is null)
            return "unavailable";
        foreach (string token in output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParseExact(token, "D", out Guid scheme))
                return scheme.ToString("D");
        }
        return "unavailable";
    }

    private static string? ReadGit(string arguments)
    {
        string root;
        try
        {
            root = BenchmarkOptions.FindRepositoryRoot(AppContext.BaseDirectory);
        }
        catch
        {
            return null;
        }
        return RunProcess("git.exe", arguments, root)?.Trim();
    }

    private static string? RunProcess(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
