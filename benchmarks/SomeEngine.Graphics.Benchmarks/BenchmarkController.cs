using System.Diagnostics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Benchmarks;

internal static class BenchmarkController
{
    internal static int RunWarp(BenchmarkOptions options) => RunController(
        options,
        BenchmarkProfile.WarpFunctional,
        processCount: 1,
        FixedGraphicsProtocol.WarpWarmupFrames,
        FixedGraphicsProtocol.WarpMeasuredFrames,
        FixedGraphicsProtocol.WarpDrawCount,
        FixedGraphicsProtocol.WarpBarrierCount);

    internal static int RunCertification(BenchmarkOptions options) => RunController(
        options,
        BenchmarkProfile.VendorCertification,
        FixedGraphicsProtocol.ProcessCount,
        FixedGraphicsProtocol.WarmupFrames,
        FixedGraphicsProtocol.MeasuredFrames,
        FixedGraphicsProtocol.DrawCount,
        FixedGraphicsProtocol.BarrierCount);

    internal static int EvaluateExisting(BenchmarkOptions options)
    {
        GraphicsBenchmarkReport report = GraphicsBenchmarkReport.Read(options.InputPath!);
        GateResult gate = BenchmarkGate.Evaluate(report.Profile, report.Protocol, report.Runs);
        Console.WriteLine(gate.Reason);
        foreach (GateIssue issue in gate.Issues)
            Console.WriteLine($"{issue.Code}: {issue.Message}");
        return gate.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly ? 0 : 3;
    }

    private static int RunController(
        BenchmarkOptions options,
        BenchmarkProfile profile,
        int processCount,
        int warmup,
        int measured,
        int draws,
        int barriers)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows())
            return WriteUnexecuted(options, profile, started, "Direct3D 12 requires Windows.");

        AdapterInfo adapter;
        try
        {
            adapter = SelectAdapter(options, profile);
        }
        catch (Exception exception)
        {
            return WriteUnexecuted(options, profile, started, exception.Message);
        }
        if (profile == BenchmarkProfile.VendorCertification && !adapter.HardwareAccelerated)
            return WriteUnexecuted(options, profile, started, "The selected adapter is software/WARP.");

        Directory.CreateDirectory(options.ShaderDirectory);
        BenchmarkShaders.EmitSharedArtifacts(options.ShaderDirectory);
        string runDirectory;
        if (options.ResumeDirectory is null)
        {
            runDirectory = Path.Combine(
                Path.GetDirectoryName(options.OutputPath)!,
                $"raw-{started:yyyyMMdd-HHmmss-fff}");
            Directory.CreateDirectory(runDirectory);
        }
        else
        {
            runDirectory = options.ResumeDirectory;
            if (!Directory.Exists(runDirectory))
                throw new DirectoryNotFoundException($"The certification resume directory '{runDirectory}' does not exist.");
            started = Directory.GetCreationTimeUtc(runDirectory);
        }
        var runs = new List<ProcessRun>(checked(processCount * FixedGraphicsProtocol.Variants.Length));

        for (int processIndex = 0; processIndex < processCount; processIndex++)
        {
            ReadOnlySpan<ReceiverVariant> round = FixedGraphicsProtocol.GetInterleavedRound(processIndex);
            for (int position = 0; position < round.Length; position++)
            {
                ReceiverVariant variant = round[position];
                string childOutput = Path.Combine(
                    runDirectory,
                    $"{processIndex:D2}-{position:D2}-{VariantName(variant)}.json");
                ProcessRun run;
                if (File.Exists(childOutput))
                {
                    run = GraphicsBenchmarkReport.ReadProcess(childOutput);
                    if (run.Variant != variant || run.Environment.ProcessIndex != processIndex)
                    {
                        throw new InvalidDataException(
                            $"Resume evidence '{childOutput}' does not identify {variant} process {processIndex}.");
                    }
                }
                else
                {
                    run = ExecuteWorker(
                        options,
                        profile,
                        variant,
                        adapter,
                        processIndex,
                        warmup,
                        measured,
                        draws,
                        barriers,
                        childOutput);
                }
                runs.Add(run);
                Console.WriteLine($"[{processIndex + 1}/{processCount}] {variant}: {run.Disposition} — {run.Reason}");
            }
        }

        ProtocolSnapshot protocol = ProtocolSnapshot.Create(
            warmup,
            measured,
            processCount,
            draws,
            barriers);
        GateResult gate = BenchmarkGate.Evaluate(profile, protocol, CollectionsMarshal.AsSpan(runs));
        GraphicsBenchmarkReport report = new(
            FixedGraphicsProtocol.Schema,
            profile,
            gate.Disposition,
            gate.Reason,
            started,
            DateTimeOffset.UtcNow,
            protocol,
            [.. runs],
            gate);
        report.Write(options.OutputPath);
        Console.WriteLine($"Graphics benchmark report: {options.OutputPath}");
        Console.WriteLine(report.Reason);
        foreach (GateIssue issue in report.Gate.Issues)
            Console.WriteLine($"{issue.Code}: {issue.Message}");
        return report.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly ? 0 : 3;
    }

    private static ProcessRun ExecuteWorker(
        BenchmarkOptions options,
        BenchmarkProfile profile,
        ReceiverVariant variant,
        in AdapterInfo adapter,
        int processIndex,
        int warmup,
        int measured,
        int draws,
        int barriers,
        string childOutput)
    {
        string? executable = variant == ReceiverVariant.NativeCpp
            ? options.NativeRunnerPath
            : options.ManagedRunnerPath ?? ResolveManagedExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return new ProcessRun(
                variant,
                RunDisposition.Unexecuted,
                variant == ReceiverVariant.NativeCpp
                    ? "The native C++ runner was not supplied or does not exist."
                    : "The managed benchmark executable could not be resolved.",
                BenchmarkEnvironment.Unavailable(processIndex),
                []);
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = BenchmarkOptions.FindRepositoryRoot(AppContext.BaseDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (variant != ReceiverVariant.NativeCpp)
            start.ArgumentList.Add("worker");
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profile == BenchmarkProfile.VendorCertification ? "certify" : "warp");
        start.ArgumentList.Add("--variant");
        start.ArgumentList.Add(VariantName(variant));
        start.ArgumentList.Add("--adapter");
        start.ArgumentList.Add($"0x{adapter.Id.Low:X}:0x{adapter.Id.High:X}");
        start.ArgumentList.Add("--process-index");
        start.ArgumentList.Add(processIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--warmup");
        start.ArgumentList.Add(warmup.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--samples");
        start.ArgumentList.Add(measured.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--draws");
        start.ArgumentList.Add(draws.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--barriers");
        start.ArgumentList.Add(barriers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--shader-dir");
        start.ArgumentList.Add(options.ShaderDirectory);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(childOutput);

        try
        {
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (File.Exists(childOutput))
                return GraphicsBenchmarkReport.ReadProcess(childOutput);
            string reason = $"Runner exit code {process.ExitCode}. {stderr.Result.Trim()}".Trim();
            return new ProcessRun(
                variant,
                RunDisposition.Unexecuted,
                reason,
                BenchmarkEnvironment.Unavailable(processIndex),
                []);
        }
        catch (Exception exception)
        {
            return new ProcessRun(
                variant,
                RunDisposition.Unexecuted,
                exception.Message,
                BenchmarkEnvironment.Unavailable(processIndex),
                []);
        }
    }

    private static AdapterInfo SelectAdapter(BenchmarkOptions options, BenchmarkProfile profile)
    {
        using D3D12Backend backend = new();
        AdapterEnumerationOptions enumeration = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(enumeration, [], out int count);
        AdapterInfo[] adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(enumeration, adapters, out int confirmed) || confirmed != count)
            throw new InvalidOperationException("The adapter inventory changed during enumeration.");
        if (options.AdapterSpecified)
        {
            foreach (AdapterInfo adapter in adapters)
            {
                if (adapter.Id == options.AdapterId)
                    return adapter;
            }
            string inventory = string.Join(
                "; ",
                adapters.Select(static adapter =>
                    $"{adapter.Name} [0x{adapter.Id.Low:X}:0x{adapter.Id.High:X}] " +
                    $"({(adapter.HardwareAccelerated ? "hardware" : "software")})"));
            throw new NotSupportedException(
                $"The explicitly selected adapter is unavailable. Available adapters: {inventory}.");
        }
        if (profile == BenchmarkProfile.WarpFunctional)
        {
            foreach (AdapterInfo adapter in adapters)
            {
                if (!adapter.HardwareAccelerated)
                    return adapter;
            }
            throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
        }
        throw new BenchmarkUsageException("Vendor certification requires an explicit adapter LUID.");
    }

    private static int WriteUnexecuted(
        BenchmarkOptions options,
        BenchmarkProfile profile,
        DateTimeOffset started,
        string reason)
    {
        ProtocolSnapshot protocol = ProtocolSnapshot.Create(
            profile == BenchmarkProfile.VendorCertification ? FixedGraphicsProtocol.WarmupFrames : FixedGraphicsProtocol.WarpWarmupFrames,
            profile == BenchmarkProfile.VendorCertification ? FixedGraphicsProtocol.MeasuredFrames : FixedGraphicsProtocol.WarpMeasuredFrames,
            profile == BenchmarkProfile.VendorCertification ? FixedGraphicsProtocol.ProcessCount : 1,
            profile == BenchmarkProfile.VendorCertification ? FixedGraphicsProtocol.DrawCount : FixedGraphicsProtocol.WarpDrawCount,
            profile == BenchmarkProfile.VendorCertification ? FixedGraphicsProtocol.BarrierCount : FixedGraphicsProtocol.WarpBarrierCount);
        GateResult gate = new(
            RunDisposition.Unexecuted,
            reason,
            [new GateIssue("RHI-EVID-ENVIRONMENT", reason)],
            []);
        new GraphicsBenchmarkReport(
            FixedGraphicsProtocol.Schema,
            profile,
            RunDisposition.Unexecuted,
            reason,
            started,
            DateTimeOffset.UtcNow,
            protocol,
            [],
            gate).Write(options.OutputPath);
        Console.WriteLine(reason);
        Console.WriteLine($"Graphics benchmark report: {options.OutputPath}");
        return 2;
    }

    private static string ResolveManagedExecutable()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The benchmark controller requires an apphost/published executable, not 'dotnet <dll>'.");
        }
        return Path.GetFullPath(path);
    }

    internal static string VariantName(ReceiverVariant variant) => variant switch
    {
        ReceiverVariant.GenericRhi => "generic-rhi",
        ReceiverVariant.InterfaceRhi => "interface-rhi",
        ReceiverVariant.DirectSilk => "direct-silk",
        ReceiverVariant.NativeCpp => "native-cpp",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };
}
