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

    internal static int RunDiagnostic(BenchmarkOptions options) => RunController(
        options,
        BenchmarkProfile.FastDiagnostic,
        FixedGraphicsProtocol.DiagnosticProcessCount,
        FixedGraphicsProtocol.DiagnosticWarmupFrames,
        FixedGraphicsProtocol.DiagnosticMeasuredFrames,
        FixedGraphicsProtocol.DiagnosticDrawCount,
        FixedGraphicsProtocol.DiagnosticBarrierCount);

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
        ProcessGateEvidence[] evidence = LoadRawEvidence(options.InputPath!, report);
        GateResult gate = BenchmarkGate.Evaluate(report.Profile, report.Protocol, evidence);
        PrintGate(gate);
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
        if (profile != BenchmarkProfile.WarpFunctional && !adapter.HardwareAccelerated)
        {
            return WriteUnexecuted(
                options,
                profile,
                started,
                "Vendor certification and fast diagnostics require a hardware adapter; the selected adapter is software/WARP.");
        }

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
                throw new DirectoryNotFoundException($"The benchmark resume directory '{runDirectory}' does not exist.");
            started = Directory.GetCreationTimeUtc(runDirectory);
        }
        var gateEvidence = new List<ProcessGateEvidence>(
            checked(processCount * FixedGraphicsProtocol.Variants.Length));
        var rawEvidence = new List<RawProcessEvidence>(gateEvidence.Capacity);
        string reportDirectory = Path.GetDirectoryName(options.OutputPath)!;

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
                    if (!CanReuseProcess(
                            run,
                            options,
                            profile,
                            variant,
                            adapter,
                            warmup,
                            measured,
                            draws,
                            barriers))
                    {
                        File.Delete(childOutput);
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
                if (!File.Exists(childOutput))
                    GraphicsBenchmarkReport.WriteProcess(childOutput, run);
                gateEvidence.Add(ProcessGateEvidence.Create(run, position));
                rawEvidence.Add(new RawProcessEvidence(
                    Path.GetRelativePath(reportDirectory, childOutput).Replace('\\', '/'),
                    BenchmarkEnvironment.Sha256File(childOutput),
                    variant,
                    processIndex,
                    position));
                Console.WriteLine($"[{processIndex + 1}/{processCount}] {variant}: {run.Disposition} — {run.Reason}");
            }
        }

        ProtocolSnapshot protocol = ProtocolSnapshot.Create(
            profile,
            warmup,
            measured,
            processCount,
            draws,
            barriers);
        GateResult gate = BenchmarkGate.Evaluate(
            profile,
            protocol,
            CollectionsMarshal.AsSpan(gateEvidence));
        GraphicsBenchmarkReport report = new(
            FixedGraphicsProtocol.Schema,
            profile,
            gate.Disposition,
            gate.Reason,
            started,
            DateTimeOffset.UtcNow,
            protocol,
            [.. rawEvidence],
            gate);
        report.Write(options.OutputPath);
        Console.WriteLine($"Graphics benchmark report: {options.OutputPath}");
        PrintGate(report.Gate);
        return report.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly ? 0 : 3;
    }

    private static bool CanReuseProcess(
        ProcessRun run,
        BenchmarkOptions options,
        BenchmarkProfile profile,
        ReceiverVariant variant,
        in AdapterInfo adapter,
        int warmup,
        int measured,
        int draws,
        int barriers)
    {
        string? executable = variant == ReceiverVariant.NativeCpp
            ? options.NativeRunnerPath
            : options.ManagedRunnerPath ?? ResolveManagedExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            run.Disposition is not RunDisposition.Passed and not RunDisposition.FunctionalOnly ||
            run.Environment.AdapterLuidLow != adapter.Id.Low ||
            run.Environment.AdapterLuidHigh != adapter.Id.High ||
            !string.Equals(
                run.Environment.Build.ExecutableSha256,
                BenchmarkEnvironment.Sha256File(executable),
                StringComparison.Ordinal))
        {
            return false;
        }
        if (variant != ReceiverVariant.NativeCpp &&
            !string.Equals(
                run.Environment.Build.PayloadSha256,
                BenchmarkEnvironment.BuildPayloadSha256(Path.GetDirectoryName(executable)!),
                StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<GraphicsWorkload> expected = FixedGraphicsProtocol.GetWorkloads(profile);
        if (run.Workloads.Length != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
        {
            WorkloadRun workload = run.Workloads[index];
            if (workload.Workload != expected[index] ||
                workload.Disposition is not RunDisposition.Passed and not RunDisposition.FunctionalOnly ||
                workload.WarmupFrames != warmup ||
                workload.MeasuredFrames != measured ||
                workload.DrawCount != draws ||
                workload.BarrierCount != barriers ||
                workload.Samples.Length != measured)
            {
                return false;
            }
        }
        return true;
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
        start.ArgumentList.Add(ProfileName(profile));
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
        throw new BenchmarkUsageException("Vendor certification and fast diagnostics require an explicit adapter LUID.");
    }

    private static int WriteUnexecuted(
        BenchmarkOptions options,
        BenchmarkProfile profile,
        DateTimeOffset started,
        string reason)
    {
        (int warmup, int measured, int processCount, int draws, int barriers) = profile switch
        {
            BenchmarkProfile.WarpFunctional => (
                FixedGraphicsProtocol.WarpWarmupFrames,
                FixedGraphicsProtocol.WarpMeasuredFrames,
                1,
                FixedGraphicsProtocol.WarpDrawCount,
                FixedGraphicsProtocol.WarpBarrierCount),
            BenchmarkProfile.FastDiagnostic => (
                FixedGraphicsProtocol.DiagnosticWarmupFrames,
                FixedGraphicsProtocol.DiagnosticMeasuredFrames,
                FixedGraphicsProtocol.DiagnosticProcessCount,
                FixedGraphicsProtocol.DiagnosticDrawCount,
                FixedGraphicsProtocol.DiagnosticBarrierCount),
            BenchmarkProfile.VendorCertification => (
                FixedGraphicsProtocol.WarmupFrames,
                FixedGraphicsProtocol.MeasuredFrames,
                FixedGraphicsProtocol.ProcessCount,
                FixedGraphicsProtocol.DrawCount,
                FixedGraphicsProtocol.BarrierCount),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        ProtocolSnapshot protocol = ProtocolSnapshot.Create(
            profile,
            warmup,
            measured,
            processCount,
            draws,
            barriers);
        GateResult gate = new(
            RunDisposition.Unexecuted,
            reason,
            [new GateIssue("RHI-EVID-ENVIRONMENT", reason)],
            [],
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

    private static ProcessGateEvidence[] LoadRawEvidence(
        string reportPath,
        GraphicsBenchmarkReport report)
    {
        if (!string.Equals(report.Schema, FixedGraphicsProtocol.Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Report schema '{report.Schema}' is not the current '{FixedGraphicsProtocol.Schema}'.");
        }

        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        var result = new ProcessGateEvidence[report.RawEvidence.Length];
        for (int index = 0; index < report.RawEvidence.Length; index++)
        {
            RawProcessEvidence evidence = report.RawEvidence[index];
            ProcessRun run;
            try
            {
                string normalized = evidence.Path.Replace('/', Path.DirectorySeparatorChar);
                string rawPath = Path.GetFullPath(Path.Combine(reportDirectory, normalized));
                if (!File.Exists(rawPath))
                    throw new FileNotFoundException("The raw process evidence is missing.", rawPath);
                string actualHash = BenchmarkEnvironment.Sha256File(rawPath);
                if (!string.Equals(actualHash, evidence.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Raw evidence SHA-256 mismatch for '{rawPath}'.");

                ReadOnlySpan<ReceiverVariant> round =
                    FixedGraphicsProtocol.GetInterleavedRound(evidence.ProcessIndex);
                if ((uint)evidence.Position >= (uint)round.Length ||
                    round[evidence.Position] != evidence.Variant)
                {
                    throw new InvalidDataException(
                        $"Raw evidence manifest position {evidence.Position} does not identify " +
                        $"{evidence.Variant} in process {evidence.ProcessIndex}.");
                }

                run = GraphicsBenchmarkReport.ReadProcess(rawPath);
                if (run.Variant != evidence.Variant ||
                    run.Environment.ProcessIndex != evidence.ProcessIndex)
                {
                    throw new InvalidDataException(
                        $"Raw evidence '{rawPath}' does not match its manifest identity.");
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                ArgumentException or
                System.Text.Json.JsonException)
            {
                run = new ProcessRun(
                    evidence.Variant,
                    RunDisposition.Failed,
                    $"Raw evidence could not be admitted: {exception.Message}",
                    BenchmarkEnvironment.Unavailable(evidence.ProcessIndex),
                    []);
            }
            result[index] = ProcessGateEvidence.Create(run, evidence.Position);
        }
        return result;
    }

    private static void PrintGate(GateResult gate)
    {
        Console.WriteLine(gate.Reason);
        foreach (GateIssue issue in gate.Issues)
            Console.WriteLine($"{issue.Code}: {issue.Message}");
        foreach (DiagnosticBiasResult diagnostic in gate.Diagnostics)
        {
            string positions = string.Join(
                ", ",
                diagnostic.PositionEffectsPercent.Select(static (value, index) =>
                    FormattableString.Invariant($"P{index}={value:+0.###;-0.###;0}%")));
            string rounds = string.Join(
                ", ",
                diagnostic.RoundEffectsPercent.Select(static (value, index) =>
                    FormattableString.Invariant($"R{index}={value:+0.###;-0.###;0}%")));
            string variants = string.Join(
                ", ",
                diagnostic.VariantEffectsPercent.Select(static (value, index) =>
                    FormattableString.Invariant(
                        $"{FixedGraphicsProtocol.Variants[index]}={value:+0.###;-0.###;0}%")));
            Console.WriteLine(FormattableString.Invariant(
                $"DIAGNOSTIC {diagnostic.Workload}/{diagnostic.Metric}: geomean={diagnostic.GeometricMeanMicroseconds:F3} us; receiver spread={diagnostic.VariantSpreadPercent:F3}% [{variants}]; position spread={diagnostic.PositionSpreadPercent:F3}% [{positions}]; round drift={diagnostic.RoundSpreadPercent:F3}% [{rounds}]; residual RMS={diagnostic.ResidualRmsPercent:F3}%."));
        }
    }

    internal static string VariantName(ReceiverVariant variant) => variant switch
    {
        ReceiverVariant.GenericRhi => "generic-rhi",
        ReceiverVariant.InterfaceRhi => "interface-rhi",
        ReceiverVariant.DirectSilk => "direct-silk",
        ReceiverVariant.NativeCpp => "native-cpp",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static string ProfileName(BenchmarkProfile profile) => profile switch
    {
        BenchmarkProfile.WarpFunctional => "warp",
        BenchmarkProfile.FastDiagnostic => "diagnose",
        BenchmarkProfile.VendorCertification => "certify",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
