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

    internal static int RunProbe(BenchmarkOptions options) => RunController(
        options,
        BenchmarkProfile.DeveloperProbe,
        processCount: 1,
        options.WarmupFrames,
        options.MeasuredFrames,
        options.DrawCount,
        options.BarrierCount,
        options.Variants,
        options.Workloads);

    internal static int EvaluateExisting(BenchmarkOptions options)
    {
        GraphicsBenchmarkReport report = GraphicsBenchmarkReport.Read(options.InputPath!);
        ProcessGateEvidence[] evidence = LoadRawEvidence(options.InputPath!, report);
        GateResult gate = report.Profile == BenchmarkProfile.DeveloperProbe
            ? EvaluateProbe(
                evidence,
                report.Protocol.Workloads.Select(static value => Enum.Parse<GraphicsWorkload>(value)).ToArray(),
                report.Protocol.DrawCount,
                report.Protocol.BarrierCount)
            : BenchmarkGate.Evaluate(report.Profile, report.Protocol, evidence);
        gate = gate with
        {
            PairedDiagnostics = AnalyzePairedDiagnostics(
                evidence,
                report.Protocol.DrawCount,
                report.Protocol.BarrierCount),
        };
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
        int barriers,
        ReceiverVariant[]? selectedVariants = null,
        GraphicsWorkload[]? selectedWorkloads = null)
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
                "Hardware RHI measurements require a hardware adapter; the selected adapter is software/WARP.");
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
            ReadOnlySpan<ReceiverVariant> round = selectedVariants ?? FixedGraphicsProtocol.GetInterleavedRound(processIndex).ToArray();
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
                            barriers,
                            selectedWorkloads))
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
                            childOutput,
                            selectedWorkloads);
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
                        childOutput,
                        selectedWorkloads);
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
            barriers,
            selectedWorkloads,
            selectedVariants);
        GateResult gate = profile == BenchmarkProfile.DeveloperProbe
            ? EvaluateProbe(CollectionsMarshal.AsSpan(gateEvidence), selectedWorkloads!, draws, barriers)
            : BenchmarkGate.Evaluate(profile, protocol, CollectionsMarshal.AsSpan(gateEvidence));
        gate = gate with
        {
            PairedDiagnostics = AnalyzePairedDiagnostics(
                CollectionsMarshal.AsSpan(gateEvidence),
                draws,
                barriers),
        };
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
        int barriers,
        GraphicsWorkload[]? selectedWorkloads)
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

        return HasReusableProtocolShape(
            run,
            profile,
            warmup,
            measured,
            draws,
            barriers,
            selectedWorkloads);
    }

    internal static bool HasReusableProtocolShape(
        ProcessRun run,
        BenchmarkProfile profile,
        int warmup,
        int measured,
        int draws,
        int barriers,
        GraphicsWorkload[]? selectedWorkloads)
    {
        ReadOnlySpan<GraphicsWorkload> expected =
            selectedWorkloads ?? FixedGraphicsProtocol.GetWorkloads(profile).ToArray();
        if (run.Workloads.Length != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
        {
            WorkloadRun workload = run.Workloads[index];
            if (workload.Workload != expected[index] ||
                workload.Disposition is not RunDisposition.Passed and not RunDisposition.FunctionalOnly ||
                workload.WarmupFrames != warmup ||
                workload.MeasuredFrames != measured ||
                workload.DrawCount != ExpectedDrawCount(workload.Workload, draws) ||
                workload.BarrierCount != ExpectedBarrierCount(workload.Workload, barriers) ||
                workload.Samples.Length != measured)
            {
                return false;
            }
        }
        return true;
    }

    private static int ExpectedDrawCount(GraphicsWorkload workload, int configuredDraws) => workload switch
    {
        GraphicsWorkload.PersistentDraw10000 or
        GraphicsWorkload.TransientDraw10000 or
        GraphicsWorkload.StateSuppression10000 => configuredDraws,
        GraphicsWorkload.ThreeQueuePresent => 1,
        _ => 0,
    };

    private static int ExpectedBarrierCount(GraphicsWorkload workload, int configuredBarriers) => workload switch
    {
        GraphicsWorkload.EmptySubmit => 0,
        GraphicsWorkload.PersistentDraw10000 or
        GraphicsWorkload.TransientDraw10000 or
        GraphicsWorkload.StateSuppression10000 => 2,
        GraphicsWorkload.ExplicitBarrier4096 => configuredBarriers,
        GraphicsWorkload.ThreeQueuePresent => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(workload)),
    };

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
        string childOutput,
        GraphicsWorkload[]? selectedWorkloads)
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
        if (selectedWorkloads is { Length: > 0 })
        {
            start.ArgumentList.Add("--workloads");
            start.ArgumentList.Add(string.Join(',', selectedWorkloads.Select(WorkloadName)));
        }
        if (options.DefaultDirectCalls && variant == ReceiverVariant.DirectSilk)
        {
            start.ArgumentList.Add("--direct-mode");
            start.ArgumentList.Add("default");
        }

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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        throw new BenchmarkUsageException("Hardware RHI measurements require an explicit adapter LUID.");
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
            BenchmarkProfile.DeveloperProbe => (
                options.WarmupFrames,
                options.MeasuredFrames,
                1,
                options.DrawCount,
                options.BarrierCount),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        ProtocolSnapshot protocol = ProtocolSnapshot.Create(
            profile,
            warmup,
            measured,
            processCount,
            draws,
            barriers,
            profile == BenchmarkProfile.DeveloperProbe ? options.Workloads : null,
            profile == BenchmarkProfile.DeveloperProbe ? options.Variants : null);
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

                ReceiverVariant[] round = report.Protocol.InterleavedRounds[evidence.ProcessIndex]
                    .Select(static value => Enum.Parse<ReceiverVariant>(value))
                    .ToArray();
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
        foreach (PairedBlockDiagnostic pair in gate.PairedDiagnostics)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"PAIRED block={pair.ProcessIndex} workload={pair.Workload}: {pair.CandidateVariant}-{pair.BaselineVariant}={pair.DeltaMicrosecondsPerCall:+0.######;-0.######;0} us/call ({pair.DeltaPercent:+0.###;-0.###;0}%); positions {pair.BaselineVariant}={pair.BaselinePosition}, {pair.CandidateVariant}={pair.CandidatePosition}; P95/P50={pair.BaselineP95OverP50:0.###}/{pair.CandidateP95OverP50:0.###}."));
        }
    }

    internal static string VariantName(ReceiverVariant variant) => variant switch
    {
        ReceiverVariant.InterfaceReceiver => "interface-receiver",
        ReceiverVariant.DirectSilk => "direct-silk",
        ReceiverVariant.DirectSilkDefault => "direct-silk-default",
        ReceiverVariant.NativeCpp => "native-cpp",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static string WorkloadName(GraphicsWorkload workload) => workload switch
    {
        GraphicsWorkload.EmptySubmit => "empty-submit",
        GraphicsWorkload.PersistentDraw10000 => "persistent-draw",
        GraphicsWorkload.TransientDraw10000 => "transient-draw",
        GraphicsWorkload.StateSuppression10000 => "state-suppression",
        GraphicsWorkload.ExplicitBarrier4096 => "explicit-barrier",
        GraphicsWorkload.ThreeQueuePresent => "three-queue-present",
        _ => throw new ArgumentOutOfRangeException(nameof(workload)),
    };

    private static string ProfileName(BenchmarkProfile profile) => profile switch
    {
        BenchmarkProfile.WarpFunctional => "warp",
        BenchmarkProfile.FastDiagnostic => "diagnose",
        BenchmarkProfile.VendorCertification => "certify",
        BenchmarkProfile.DeveloperProbe => "probe",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static GateResult EvaluateProbe(
        ReadOnlySpan<ProcessGateEvidence> evidence,
        GraphicsWorkload[] workloads,
        int draws,
        int barriers)
    {
        var comparisons = new List<ComparisonResult>();
        ProcessGateEvidence[] runs = evidence.ToArray();
        if (runs.Length == 0)
        {
            const string missing = "Developer probe has no raw evidence; it is non-gating and cannot certify.";
            return new GateResult(
                RunDisposition.Unexecuted,
                missing,
                [new GateIssue("RHI-PROBE-NO-EVIDENCE", missing)],
                [],
                []);
        }
        ReceiverVariant baselineVariant = runs[0].Variant;
        foreach (GraphicsWorkload workload in workloads)
        {
            WorkloadGateEvidence? baseline = runs[0].Workloads.FirstOrDefault(value => value.Workload == workload);
            if (baseline is null || baseline.CpuSamples.Length == 0)
                continue;
            double baselineP50 = MetricDistribution.From(baseline.CpuSamples).P50;
            MetricDistribution baselineDistribution = MetricDistribution.From(baseline.CpuSamples);
            int calls = workload == GraphicsWorkload.ExplicitBarrier4096 ? barriers :
                workload is GraphicsWorkload.EmptySubmit or GraphicsWorkload.ThreeQueuePresent ? 1 : draws;
            foreach (ProcessGateEvidence run in runs.Skip(1))
            {
                WorkloadGateEvidence? candidate = run.Workloads.FirstOrDefault(value => value.Workload == workload);
                if (candidate is null || candidate.CpuSamples.Length == 0)
                    continue;
                MetricDistribution distribution = MetricDistribution.From(candidate.CpuSamples);
                double candidateP50 = distribution.P50;
                comparisons.Add(new ComparisonResult(
                    $"probe {run.Variant} vs {baselineVariant}", workload, "CPU us/call", "P50",
                    candidateP50 / calls, baselineP50 / calls, (candidateP50 - baselineP50) / calls,
                    baselineP50 == 0 ? null : (candidateP50 / baselineP50 - 1) * 100,
                    0, null, Passed: false));
                Console.WriteLine(FormattableString.Invariant(
                    $"PROBE {workload}: {run.Variant} vs {baselineVariant}; median delta={(candidateP50 - baselineP50) / calls:+0.######;-0.######;0} us/call ({(candidateP50 / baselineP50 - 1) * 100:+0.###;-0.###;0}%); P95/P50 baseline={baselineDistribution.P95 / baselineDistribution.P50:0.###}, candidate={distribution.P95 / distribution.P50:0.###}; order={string.Join(" -> ", runs.Select(static value => value.Variant))}."));
            }
            string[] hashes = runs.SelectMany(static run => run.Workloads)
                .Where(value => value.Workload == workload)
                .Select(static value => value.OutputSha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            bool gpuVisible = workload is GraphicsWorkload.PersistentDraw10000 or
                GraphicsWorkload.TransientDraw10000 or
                GraphicsWorkload.StateSuppression10000 or
                GraphicsWorkload.ThreeQueuePresent;
            Console.WriteLine(
                $"EQUIVALENCE {workload}: {(hashes.Length == 1 ? "matching" : "mismatched")} outputs; " +
                (gpuVisible
                    ? "proof is an untimed GPU-output readback hash."
                    : "no native work counter is directly observable; the hash is protocol identity, while submitted counts/statistics provide the available work proof."));
        }
        bool failed = runs.Any(static run => run.Disposition is RunDisposition.Failed or RunDisposition.Unexecuted);
        string reason = failed
            ? "Developer probe did not complete; it is explicitly non-gating and can never certify."
            : "Developer probe completed; results are exploratory, explicitly non-gating, and can never emit certification PASS.";
        return new GateResult(
            failed ? RunDisposition.Failed : RunDisposition.FunctionalOnly,
            reason,
            [new GateIssue("RHI-PROBE-NON-GATING", reason)],
            [.. comparisons],
            []);
    }

    private static PairedBlockDiagnostic[] AnalyzePairedDiagnostics(
        ReadOnlySpan<ProcessGateEvidence> evidence,
        int draws,
        int barriers)
    {
        var results = new List<PairedBlockDiagnostic>();
        ProcessGateEvidence[] runs = evidence.ToArray();
        foreach (IGrouping<int, ProcessGateEvidence> block in runs
            .Where(static run => run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .GroupBy(static run => run.Environment.ProcessIndex)
            .OrderBy(static group => group.Key))
        {
            ProcessGateEvidence? direct = block.FirstOrDefault(static run => run.Variant == ReceiverVariant.DirectSilk);
            ProcessGateEvidence? interfaceReceiver = block.FirstOrDefault(static run => run.Variant == ReceiverVariant.InterfaceReceiver);
            if (direct is null || interfaceReceiver is null)
                continue;
            foreach (WorkloadGateEvidence left in direct.Workloads)
            {
                WorkloadGateEvidence? right = interfaceReceiver.Workloads.FirstOrDefault(value => value.Workload == left.Workload);
                if (right is null || left.CpuSamples.Length == 0 || right.CpuSamples.Length == 0)
                    continue;
                double leftP50 = MetricDistribution.From(left.CpuSamples).P50;
                double rightP50 = MetricDistribution.From(right.CpuSamples).P50;
                MetricDistribution leftDistribution = MetricDistribution.From(left.CpuSamples);
                MetricDistribution rightDistribution = MetricDistribution.From(right.CpuSamples);
                int calls = left.Workload == GraphicsWorkload.ExplicitBarrier4096 ? barriers :
                    left.Workload is GraphicsWorkload.EmptySubmit or GraphicsWorkload.ThreeQueuePresent ? 1 : draws;
                results.Add(new PairedBlockDiagnostic(
                    block.Key,
                    left.Workload,
                    ReceiverVariant.DirectSilk,
                    direct.Position,
                    ReceiverVariant.InterfaceReceiver,
                    interfaceReceiver.Position,
                    (rightP50 - leftP50) / calls,
                    (rightP50 / leftP50 - 1) * 100,
                    leftDistribution.P95 / leftDistribution.P50,
                    rightDistribution.P95 / rightDistribution.P50));
            }
        }
        return [.. results];
    }
}
