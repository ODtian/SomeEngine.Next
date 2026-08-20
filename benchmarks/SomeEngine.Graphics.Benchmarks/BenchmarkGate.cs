namespace SomeEngine.Graphics.Benchmarks;

internal static class BenchmarkGate
{
    private readonly record struct Limits(
        double P50Absolute,
        double? P50Relative,
        double P95Absolute,
        double? P95Relative,
        double P99Absolute,
        double? P99Relative);

    private static readonly Limits InterfaceVsSilk = new(50, 3, 100, 5, 200, 8);
    private static readonly Limits InterfaceVsCpp = new(100, 5, 200, 8, 300, 10);
    private static readonly Limits SilkVsCpp = new(50, 3, 100, 5, 200, 8);
    private static readonly Limits GpuLimits = new(20, 1, 50, 2, 100, 3);
    private static readonly Limits EmptyInterfaceVsSilk = new(1, null, 2, null, 4, null);
    private static readonly Limits EmptyInterfaceVsCpp = new(2, null, 3, null, 6, null);
    private static readonly Limits EmptySilkVsCpp = new(1, null, 2, null, 4, null);

    internal static GateResult Evaluate(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ReadOnlySpan<ProcessRun> runs)
    {
        var evidence = new ProcessGateEvidence[runs.Length];
        for (int index = 0; index < runs.Length; index++)
            evidence[index] = ProcessGateEvidence.Create(runs[index]);
        return Evaluate(profile, protocol, evidence);
    }

    internal static GateResult Evaluate(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ReadOnlySpan<ProcessGateEvidence> runs)
    {
        var issues = new List<GateIssue>();
        var comparisons = new List<ComparisonResult>();

        ValidateRunInventory(profile, protocol, runs, issues);
        ValidateEnvironmentCohort(profile, runs, issues);
        foreach (ProcessGateEvidence run in runs)
            ValidateRun(profile, protocol, run, issues);

        foreach (GraphicsWorkload workload in FixedGraphicsProtocol.GetWorkloads(profile))
        {
            WorkloadAggregate? interfaceReceiver = Aggregate(runs, ReceiverVariant.InterfaceReceiver, workload, issues);
            WorkloadAggregate? silk = Aggregate(runs, ReceiverVariant.DirectSilk, workload, issues);
            WorkloadAggregate? cpp = Aggregate(runs, ReceiverVariant.NativeCpp, workload, issues);
            if (interfaceReceiver is null || silk is null || cpp is null)
                continue;

            RequireEquivalentOutput(workload, interfaceReceiver, silk, cpp, issues);
            if (profile != BenchmarkProfile.VendorCertification)
                continue;

            CompareCpu(
                "Interface RHI vs Direct Silk",
                workload,
                interfaceReceiver.Cpu,
                silk.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptyInterfaceVsSilk : InterfaceVsSilk,
                comparisons,
                issues);
            CompareCpu(
                "Interface RHI vs C++ D3D12",
                workload,
                interfaceReceiver.Cpu,
                cpp.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptyInterfaceVsCpp : InterfaceVsCpp,
                comparisons,
                issues);
            CompareCpu(
                "Direct Silk vs C++ D3D12",
                workload,
                silk.Cpu,
                cpp.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptySilkVsCpp : SilkVsCpp,
                comparisons,
                issues);

            if (workload != GraphicsWorkload.EmptySubmit)
            {
                if (interfaceReceiver.Gpu is null || silk.Gpu is null || cpp.Gpu is null)
                {
                    issues.Add(new GateIssue(
                        "RHI-EVID-GPU-MISSING",
                        "A non-empty workload is missing raw GPU timestamps.",
                        Workload: workload));
                }
                else
                {
                    CompareDistribution(
                        "Interface RHI GPU vs Direct Silk",
                        workload,
                        "GPU",
                        interfaceReceiver.Gpu.Value,
                        silk.Gpu.Value,
                        GpuLimits,
                        comparisons,
                        issues);
                    CompareDistribution(
                        "Interface RHI GPU vs C++ D3D12",
                        workload,
                        "GPU",
                        interfaceReceiver.Gpu.Value,
                        cpp.Gpu.Value,
                        GpuLimits,
                        comparisons,
                        issues);
                }
            }
        }

        DiagnosticBiasResult[] diagnostics = profile == BenchmarkProfile.FastDiagnostic
            ? AnalyzeDiagnosticBias(runs)
            : [];

        if (profile == BenchmarkProfile.WarpFunctional)
        {
            return new GateResult(
                issues.Count == 0 ? RunDisposition.FunctionalOnly : RunDisposition.Failed,
                issues.Count == 0
                    ? "All three receivers are functionally equivalent on WARP; WARP is not vendor performance evidence."
                    : "WARP functional equivalence failed.",
                [.. issues],
                [.. comparisons],
                diagnostics);
        }

        if (profile == BenchmarkProfile.FastDiagnostic)
        {
            bool validDiagnostic = issues.Count == 0;
            issues.Add(new GateIssue(
                "RHI-EVID-DIAGNOSTIC-NONCERTIFICATION",
                "This draw-only 512/1024-frame diagnostic can reveal ordering or frequency bias, but can never satisfy RHI-EVID-003 vendor certification."));
            return new GateResult(
                validDiagnostic ? RunDisposition.FunctionalOnly : RunDisposition.Failed,
                validDiagnostic
                    ? "The fast hardware diagnostic completed; it is explicitly not vendor-certification evidence."
                    : "The fast hardware diagnostic failed and is not vendor-certification evidence.",
                [.. issues],
                [.. comparisons],
                diagnostics);
        }

        return new GateResult(
            issues.Count == 0 ? RunDisposition.Passed : RunDisposition.Failed,
            issues.Count == 0
                ? "The fixed vendor-hardware performance protocol passed."
                : "The fixed vendor-hardware performance protocol did not pass.",
            [.. issues],
            [.. comparisons],
            diagnostics);
    }

    private static void ValidateRunInventory(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ReadOnlySpan<ProcessGateEvidence> runs,
        List<GateIssue> issues)
    {
        int expectedProcesses = FixedGraphicsProtocol.GetProcessCount(profile);
        foreach (ReceiverVariant variant in FixedGraphicsProtocol.Variants)
        {
            int count = 0;
            var processIndices = new HashSet<int>();
            foreach (ProcessGateEvidence run in runs)
            {
                if (run.Variant == variant)
                {
                    count++;
                    _ = processIndices.Add(run.Environment.ProcessIndex);
                }
            }
            if (count != expectedProcesses)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-PROCESS-COUNT",
                    $"{variant} has {count} process result(s); {expectedProcesses} are required.",
                    variant));
            }
            if (count == expectedProcesses &&
                (processIndices.Count != expectedProcesses ||
                 Enumerable.Range(0, expectedProcesses).Any(index => !processIndices.Contains(index))))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-PROCESS-INDEX",
                    $"{variant} does not contain each required process index exactly once.",
                    variant));
            }
        }

        foreach (ProcessGateEvidence run in runs)
        {
            int processIndex = run.Environment.ProcessIndex;
            if ((uint)processIndex >= (uint)expectedProcesses ||
                (uint)run.Position >= (uint)FixedGraphicsProtocol.Variants.Length ||
                FixedGraphicsProtocol.GetInterleavedRound(processIndex)[run.Position] != run.Variant)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-PROCESS-POSITION",
                    $"{run.Variant} process {processIndex} does not occupy its recorded interleaved position.",
                    run.Variant));
            }
        }

        if (protocol.ProcessCount != expectedProcesses ||
            !HasExactInterleavedSchedule(protocol.InterleavedRounds, expectedProcesses))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-PROTOCOL-ORDER",
                "The report does not record the fixed deterministic interleaved process schedule."));
        }

        if (!HasExactWorkloadInventory(protocol.Workloads, FixedGraphicsProtocol.GetWorkloads(profile)))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-PROTOCOL-WORKLOADS",
                "The report protocol does not record the exact workload inventory for its profile."));
        }

        if (profile == BenchmarkProfile.VendorCertification &&
            (protocol.WarmupFrames != FixedGraphicsProtocol.WarmupFrames ||
             protocol.MeasuredFrames != FixedGraphicsProtocol.MeasuredFrames ||
             protocol.ProcessCount != FixedGraphicsProtocol.ProcessCount ||
             protocol.DrawCount != FixedGraphicsProtocol.DrawCount ||
             protocol.BarrierCount != FixedGraphicsProtocol.BarrierCount))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-PROTOCOL-SHAPE",
                "The report does not use the fixed certification counts."));
        }
        if (profile == BenchmarkProfile.FastDiagnostic &&
            (protocol.WarmupFrames != FixedGraphicsProtocol.DiagnosticWarmupFrames ||
             protocol.MeasuredFrames != FixedGraphicsProtocol.DiagnosticMeasuredFrames ||
             protocol.ProcessCount != FixedGraphicsProtocol.DiagnosticProcessCount ||
             protocol.DrawCount != FixedGraphicsProtocol.DiagnosticDrawCount ||
             protocol.BarrierCount != FixedGraphicsProtocol.DiagnosticBarrierCount))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-DIAGNOSTIC-SHAPE",
                "The report does not use the fixed non-certification diagnostic counts."));
        }
    }

    private static void ValidateEnvironmentCohort(
        BenchmarkProfile profile,
        ReadOnlySpan<ProcessGateEvidence> runs,
        List<GateIssue> issues)
    {
        ProcessGateEvidence[] executed = runs.ToArray()
            .Where(static run => run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .ToArray();
        if (executed.Length == 0)
            return;

        RuntimeEnvironment reference = executed[0].Environment;
        foreach (ProcessGateEvidence run in executed)
        {
            RuntimeEnvironment environment = run.Environment;
            if (IsUnavailable(environment.OperatingSystem) ||
                IsUnavailable(environment.Architecture) ||
                IsUnavailable(environment.ProcessorName) ||
                environment.AffinityMask == 0 ||
                IsUnavailable(environment.Priority) ||
                IsUnavailable(environment.PowerMode) ||
                IsUnavailable(environment.AdapterName) ||
                IsUnavailable(environment.DriverVersion) ||
                IsUnavailable(environment.Build.ExecutableSha256) ||
                IsUnavailable(environment.Build.PayloadSha256) ||
                IsUnavailable(environment.Build.AssemblyVersion) ||
                IsUnavailable(environment.Build.Configuration) ||
                IsUnavailable(environment.Build.Commit) ||
                IsUnavailable(environment.Build.Toolchain))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-ENVIRONMENT-INCOMPLETE",
                    $"{run.Variant} process {environment.ProcessIndex} omitted required environment/build identity.",
                    run.Variant));
            }

            if (!SameExecutionEnvironment(reference, environment))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-ENVIRONMENT-COHORT",
                    $"{run.Variant} process {environment.ProcessIndex} did not run on the same CPU, Windows/power policy, adapter, driver, Agility SDK, or diagnostic state.",
                    run.Variant));
            }

            if (profile == BenchmarkProfile.VendorCertification &&
                (!environment.OperatingSystem.StartsWith("Microsoft Windows ", StringComparison.Ordinal) ||
                 !string.Equals(environment.Architecture, "X64", StringComparison.Ordinal) ||
                 !string.Equals(environment.Priority, "High", StringComparison.Ordinal) ||
                 environment.AgilitySdkVersion != 619))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-ENVIRONMENT-POLICY",
                    $"{run.Variant} process {environment.ProcessIndex} is not the fixed win-x64/High-priority/Agility 619 environment.",
                    run.Variant));
            }
        }

        foreach (ReceiverVariant variant in FixedGraphicsProtocol.Variants)
            ValidateVariantBuild(executed, variant, issues);
        ValidateManagedExecutable(executed, issues);
    }

    private static bool HasExactInterleavedSchedule(
        string[][] recorded,
        int processCount)
    {
        if (recorded.Length != processCount)
            return false;
        for (int processIndex = 0; processIndex < processCount; processIndex++)
        {
            ReadOnlySpan<ReceiverVariant> expected =
                FixedGraphicsProtocol.GetInterleavedRound(processIndex);
            string[] actual = recorded[processIndex];
            if (actual.Length != expected.Length)
                return false;
            for (int position = 0; position < expected.Length; position++)
            {
                if (!string.Equals(
                    actual[position],
                    expected[position].ToString(),
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool HasExactWorkloadInventory(
        ReadOnlySpan<string> recorded,
        ReadOnlySpan<GraphicsWorkload> expected)
    {
        if (recorded.Length != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(recorded[index], expected[index].ToString(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void ValidateVariantBuild(
        ProcessGateEvidence[] runs,
        ReceiverVariant variant,
        List<GateIssue> issues)
    {
        ProcessGateEvidence[] family = runs
            .Where(run => run.Variant == variant)
            .ToArray();
        if (family.Length == 0)
            return;

        BuildIdentity reference = family[0].Environment.Build;
        foreach (ProcessGateEvidence run in family)
        {
            BuildIdentity build = run.Environment.Build;
            if (!string.Equals(reference.ExecutableSha256, build.ExecutableSha256, StringComparison.Ordinal) ||
                !string.Equals(reference.PayloadSha256, build.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(reference.AssemblyVersion, build.AssemblyVersion, StringComparison.Ordinal) ||
                !string.Equals(reference.Configuration, build.Configuration, StringComparison.Ordinal) ||
                !string.Equals(reference.Commit, build.Commit, StringComparison.Ordinal) ||
                reference.WorktreeDirty != build.WorktreeDirty ||
                !string.Equals(reference.Toolchain, build.Toolchain, StringComparison.Ordinal))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-BUILD-COHORT",
                    $"{run.Variant} process {run.Environment.ProcessIndex} does not match the other {run.Variant} executable/build identities.",
                    run.Variant));
            }
        }
    }

    private static void ValidateManagedExecutable(
        ProcessGateEvidence[] runs,
        List<GateIssue> issues)
    {
        ProcessGateEvidence[] managed = runs
            .Where(static run => run.Variant != ReceiverVariant.NativeCpp)
            .ToArray();
        if (managed.Length == 0)
            return;

        BuildIdentity reference = managed[0].Environment.Build;
        foreach (ProcessGateEvidence run in managed)
        {
            BuildIdentity build = run.Environment.Build;
            if (!string.Equals(reference.ExecutableSha256, build.ExecutableSha256, StringComparison.Ordinal) ||
                !string.Equals(reference.PayloadSha256, build.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(reference.AssemblyVersion, build.AssemblyVersion, StringComparison.Ordinal) ||
                !string.Equals(reference.Configuration, build.Configuration, StringComparison.Ordinal) ||
                !string.Equals(reference.Commit, build.Commit, StringComparison.Ordinal) ||
                reference.WorktreeDirty != build.WorktreeDirty)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-BUILD-COHORT",
                    $"{run.Variant} process {run.Environment.ProcessIndex} was not launched from the shared managed executable/build.",
                    run.Variant));
            }
        }
    }

    private static bool SameExecutionEnvironment(
        in RuntimeEnvironment left,
        in RuntimeEnvironment right) =>
        string.Equals(left.OperatingSystem, right.OperatingSystem, StringComparison.Ordinal) &&
        string.Equals(left.Architecture, right.Architecture, StringComparison.Ordinal) &&
        string.Equals(left.ProcessorName, right.ProcessorName, StringComparison.Ordinal) &&
        left.AffinityMask == right.AffinityMask &&
        string.Equals(left.Priority, right.Priority, StringComparison.Ordinal) &&
        string.Equals(left.PowerMode, right.PowerMode, StringComparison.Ordinal) &&
        string.Equals(left.AdapterName, right.AdapterName, StringComparison.Ordinal) &&
        left.VendorId == right.VendorId &&
        left.DeviceId == right.DeviceId &&
        left.AdapterLuidLow == right.AdapterLuidLow &&
        left.AdapterLuidHigh == right.AdapterLuidHigh &&
        string.Equals(left.DriverVersion, right.DriverVersion, StringComparison.Ordinal) &&
        left.HardwareAccelerated == right.HardwareAccelerated &&
        left.AgilitySdkVersion == right.AgilitySdkVersion &&
        left.ValidationEnabled == right.ValidationEnabled &&
        left.DredEnabled == right.DredEnabled &&
        left.CaptureToolLoaded == right.CaptureToolLoaded &&
        string.Equals(left.Build.Commit, right.Build.Commit, StringComparison.Ordinal) &&
        left.Build.WorktreeDirty == right.Build.WorktreeDirty;

    private static bool IsUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRun(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ProcessGateEvidence run,
        List<GateIssue> issues)
    {
        if (run.Disposition is RunDisposition.Unexecuted or RunDisposition.Failed)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-RUN-NOT-EXECUTED",
                $"{run.Variant} process {run.Environment.ProcessIndex}: {run.Reason}",
                run.Variant));
            return;
        }
        if (profile == BenchmarkProfile.FastDiagnostic && !run.Environment.HardwareAccelerated)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-DIAGNOSTIC-HARDWARE",
                "The ordering/frequency diagnostic requires the explicitly selected hardware adapter.",
                run.Variant));
        }
        if (profile == BenchmarkProfile.VendorCertification)
        {
            if (!run.Environment.HardwareAccelerated)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-WARP-NOT-PERFORMANCE",
                    "A WARP/software adapter cannot satisfy vendor performance acceptance.",
                    run.Variant));
            }
            if (!string.Equals(run.Environment.Build.Configuration, "Release", StringComparison.Ordinal))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-NOT-RELEASE",
                    "Vendor performance evidence must come from a Shipping Release build.",
                    run.Variant));
            }
            if (run.Environment.ValidationEnabled || run.Environment.DredEnabled || run.Environment.CaptureToolLoaded)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-DIAGNOSTICS-ENABLED",
                    "Validation, DRED, and capture tools must be disabled.",
                    run.Variant));
            }
        }

        GraphicsWorkload[] expectedWorkloads = FixedGraphicsProtocol.GetWorkloads(profile).ToArray();
        if (run.Workloads.Length != expectedWorkloads.Length ||
            run.Workloads.Select(static value => value.Workload).Distinct().Count() != run.Workloads.Length ||
            run.Workloads.Any(value => !expectedWorkloads.Contains(value.Workload)))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-WORKLOAD-INVENTORY",
                $"{run.Variant} did not report exactly the workloads selected by the report profile.",
                run.Variant));
        }
        foreach (GraphicsWorkload workload in expectedWorkloads)
        {
            WorkloadGateEvidence[] matches = run.Workloads
                .Where(value => value.Workload == workload)
                .ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-WORKLOAD-MISSING",
                    $"{run.Variant} reported {matches.Length} result(s) for {workload}; exactly one is required.",
                    run.Variant,
                    workload));
                continue;
            }
            WorkloadGateEvidence result = matches[0];
            if (result.Disposition is RunDisposition.Unexecuted or RunDisposition.Failed)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-WORKLOAD-FAILED",
                    $"{run.Variant}/{workload}: {result.Reason}",
                    run.Variant,
                    workload));
                continue;
            }
            if (result.WarmupFrames != protocol.WarmupFrames ||
                result.MeasuredFrames != protocol.MeasuredFrames ||
                result.SampleCount != protocol.MeasuredFrames)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-SAMPLE-COUNT",
                    $"{run.Variant}/{workload} has the wrong warm-up or measured sample count.",
                    run.Variant,
                    workload));
            }
            if (workload is GraphicsWorkload.PersistentDraw10000 or
                GraphicsWorkload.TransientDraw10000 or
                GraphicsWorkload.StateSuppression10000 &&
                result.DrawCount != protocol.DrawCount)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-DRAW-COUNT",
                    $"{run.Variant}/{workload} recorded {result.DrawCount} draws; {protocol.DrawCount} are required.",
                    run.Variant,
                    workload));
            }
            if (result.FirstAllocationFrame is int allocationFrame)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-ALLOCATION",
                    $"{run.Variant}/{workload} frame {allocationFrame} allocated " +
                    $"{result.FirstManagedAllocatedBytes} B and observed " +
                    $"{result.FirstEtwAllocationEvents} allocation event(s).",
                    run.Variant,
                    workload));
            }
            if (workload != GraphicsWorkload.EmptySubmit &&
                result.MissingGpuSample)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-GPU-SAMPLES",
                    $"{run.Variant}/{workload} is missing one or more raw GPU samples.",
                    run.Variant,
                    workload));
            }
            if (workload == GraphicsWorkload.ExplicitBarrier4096 &&
                (result.Barriers.Length != result.BarrierCount ||
                 !HasContiguousOrdinals(result.Barriers)))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-BARRIER-ORDINAL",
                    $"{run.Variant} did not retain every explicit barrier ordinal.",
                    run.Variant,
                    workload));
            }
        }
    }

    private static bool HasContiguousOrdinals(ReadOnlySpan<BarrierEvidence> barriers)
    {
        for (int index = 0; index < barriers.Length; index++)
        {
            if (barriers[index].PublicOrdinal != index || barriers[index].NativeExpansionCount <= 0)
                return false;
        }
        return true;
    }

    private static DiagnosticBiasResult[] AnalyzeDiagnosticBias(
        ReadOnlySpan<ProcessGateEvidence> runs)
    {
        ProcessGateEvidence[] runArray = runs.ToArray();
        var results = new List<DiagnosticBiasResult>(
            FixedGraphicsProtocol.DiagnosticWorkloads.Length * 2);
        foreach (GraphicsWorkload workload in FixedGraphicsProtocol.DiagnosticWorkloads)
        {
            AddMetric(workload, "CPU", gpu: false);
            AddMetric(workload, "GPU", gpu: true);
        }
        return [.. results];

        void AddMetric(GraphicsWorkload workload, string metric, bool gpu)
        {
            var cells = new List<DiagnosticCell>(
                FixedGraphicsProtocol.DiagnosticProcessCount *
                FixedGraphicsProtocol.Variants.Length);
            foreach (ProcessGateEvidence run in runArray)
            {
                if (run.Disposition is not (RunDisposition.Passed or RunDisposition.FunctionalOnly))
                    continue;
                WorkloadGateEvidence? evidence = run.Workloads.FirstOrDefault(
                    value => value.Workload == workload &&
                        value.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly);
                if (evidence is null)
                    continue;
                double[] samples = gpu ? evidence.GpuSamples : evidence.CpuSamples;
                if (samples.Length == 0)
                    continue;
                double value = MetricDistribution.From(samples).P50;
                if (!double.IsFinite(value) || value <= 0)
                    continue;
                cells.Add(new DiagnosticCell(
                    run.Variant,
                    run.Environment.ProcessIndex,
                    run.Position,
                    Math.Log(value)));
            }

            int requiredCells = checked(
                FixedGraphicsProtocol.DiagnosticProcessCount *
                FixedGraphicsProtocol.Variants.Length);
            if (cells.Count != requiredCells ||
                cells.Select(static cell => (cell.Round, cell.Position)).Distinct().Count() !=
                    requiredCells)
            {
                return;
            }

            double grand = cells.Average(static cell => cell.LogMicroseconds);
            double[] positionEffects = MeansBy(
                cells,
                FixedGraphicsProtocol.Variants.Length,
                static cell => cell.Position,
                grand);
            double[] roundEffects = MeansBy(
                cells,
                FixedGraphicsProtocol.DiagnosticProcessCount,
                static cell => cell.Round,
                grand);
            double[] variantEffects = MeansBy(
                cells,
                FixedGraphicsProtocol.Variants.Length,
                static cell => (int)cell.Variant,
                grand);
            if (positionEffects.Length != FixedGraphicsProtocol.Variants.Length ||
                roundEffects.Length != FixedGraphicsProtocol.DiagnosticProcessCount ||
                variantEffects.Length != FixedGraphicsProtocol.Variants.Length)
            {
                return;
            }
            double residualSquares = 0;
            foreach (DiagnosticCell cell in cells)
            {
                double residual = cell.LogMicroseconds - grand -
                    positionEffects[cell.Position] -
                    roundEffects[cell.Round] -
                    variantEffects[(int)cell.Variant];
                double residualPercent = PercentEffect(residual);
                residualSquares += residualPercent * residualPercent;
            }

            results.Add(new DiagnosticBiasResult(
                workload,
                metric,
                Math.Exp(grand),
                positionEffects.Select(PercentEffect).ToArray(),
                SpreadPercent(positionEffects),
                roundEffects.Select(PercentEffect).ToArray(),
                SpreadPercent(roundEffects),
                variantEffects.Select(PercentEffect).ToArray(),
                SpreadPercent(variantEffects),
                Math.Sqrt(residualSquares / cells.Count)));
        }
    }

    private static double[] MeansBy(
        List<DiagnosticCell> cells,
        int count,
        Func<DiagnosticCell, int> selector,
        double grand)
    {
        var sums = new double[count];
        var counts = new int[count];
        foreach (DiagnosticCell cell in cells)
        {
            int index = selector(cell);
            if ((uint)index >= (uint)count)
                return [];
            sums[index] += cell.LogMicroseconds;
            counts[index]++;
        }
        for (int index = 0; index < count; index++)
        {
            if (counts[index] == 0)
                return [];
            sums[index] = sums[index] / counts[index] - grand;
        }
        return sums;
    }

    private static double PercentEffect(double logEffect) =>
        (Math.Exp(logEffect) - 1) * 100;

    private static double SpreadPercent(ReadOnlySpan<double> logEffects)
    {
        if (logEffects.IsEmpty)
            return double.NaN;
        double minimum = logEffects[0];
        double maximum = logEffects[0];
        foreach (double value in logEffects[1..])
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        return PercentEffect(maximum - minimum);
    }

    private static WorkloadAggregate? Aggregate(
        ReadOnlySpan<ProcessGateEvidence> runs,
        ReceiverVariant variant,
        GraphicsWorkload workload,
        List<GateIssue> issues)
    {
        WorkloadGateEvidence[] selected = runs.ToArray()
            .Where(run => run.Variant == variant && run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .SelectMany(static run => run.Workloads)
            .Where(run => run.Workload == workload && run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .ToArray();
        if (selected.Length == 0)
            return null;

        double[] cpu = selected.SelectMany(static run => run.CpuSamples).ToArray();
        double[] gpu = selected.SelectMany(static run => run.GpuSamples).ToArray();
        if (cpu.Length == 0)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-SAMPLE-COUNT",
                $"{variant}/{workload} has no CPU samples.",
                variant,
                workload));
            return null;
        }
        string[] hashes = selected.Select(static run => run.OutputSha256).Distinct(StringComparer.Ordinal).ToArray();
        if (hashes.Length != 1)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-NONDETERMINISTIC-HASH",
                $"{variant}/{workload} produced different hashes across processes.",
                variant,
                workload));
        }
        return new WorkloadAggregate(
            MetricDistribution.From(cpu),
            gpu.Length == 0 ? null : MetricDistribution.From(gpu),
            hashes.FirstOrDefault() ?? string.Empty,
            selected[0].ShaderManifestSha256,
            selected[0].Barriers);
    }

    private static void RequireEquivalentOutput(
        GraphicsWorkload workload,
        WorkloadAggregate interfaceReceiver,
        WorkloadAggregate silk,
        WorkloadAggregate cpp,
        List<GateIssue> issues)
    {
        string[] hashes = [interfaceReceiver.OutputSha256, silk.OutputSha256, cpp.OutputSha256];
        string[] shaders = [interfaceReceiver.ShaderManifestSha256, silk.ShaderManifestSha256, cpp.ShaderManifestSha256];
        if (hashes.Any(string.IsNullOrWhiteSpace) || hashes.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-OUTPUT-HASH",
                "The three receivers did not produce one exact output hash.",
                Workload: workload));
        }
        if (shaders.Any(string.IsNullOrWhiteSpace) || shaders.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-SHADER-IDENTITY",
                "The three receivers did not consume one Slang-produced DXIL manifest.",
                Workload: workload));
        }
    }

    private static void CompareCpu(
        string name,
        GraphicsWorkload workload,
        MetricDistribution candidate,
        MetricDistribution baseline,
        Limits limits,
        List<ComparisonResult> comparisons,
        List<GateIssue> issues) =>
        CompareDistribution(name, workload, "CPU", candidate, baseline, limits, comparisons, issues);

    private static void CompareDistribution(
        string name,
        GraphicsWorkload workload,
        string metric,
        MetricDistribution candidate,
        MetricDistribution baseline,
        Limits limits,
        List<ComparisonResult> comparisons,
        List<GateIssue> issues)
    {
        Compare("P50", candidate.P50, baseline.P50, limits.P50Absolute, limits.P50Relative);
        Compare("P95", candidate.P95, baseline.P95, limits.P95Absolute, limits.P95Relative);
        Compare("P99", candidate.P99, baseline.P99, limits.P99Absolute, limits.P99Relative);

        void Compare(
            string percentile,
            double candidateValue,
            double baselineValue,
            double absolute,
            double? relative)
        {
            double delta = Math.Max(0, candidateValue - baselineValue);
            double? percentage = baselineValue == 0
                ? (delta == 0 ? 0 : null)
                : delta / baselineValue * 100;
            bool passed = delta <= absolute &&
                (!relative.HasValue ||
                 (percentage.HasValue && percentage.Value <= relative.Value));
            comparisons.Add(new ComparisonResult(
                name,
                workload,
                metric,
                percentile,
                candidateValue,
                baselineValue,
                delta,
                percentage,
                absolute,
                relative,
                passed));
            if (!passed)
            {
                string percentageText = percentage.HasValue
                    ? $"{percentage.Value:F3}%"
                    : "unbounded";
                string relativeText = relative.HasValue
                    ? $"{relative.Value:F3}%"
                    : "disabled";
                issues.Add(new GateIssue(
                    "RHI-EVID-PERFORMANCE-LIMIT",
                    $"{name} {workload} {metric} {percentile}: delta {delta:F3} us/{percentageText} exceeds {absolute:F3} us/{relativeText}.",
                    Workload: workload));
            }
        }
    }

    private sealed record WorkloadAggregate(
        MetricDistribution Cpu,
        MetricDistribution? Gpu,
        string OutputSha256,
        string ShaderManifestSha256,
        BarrierEvidence[] Barriers);

    private readonly record struct DiagnosticCell(
        ReceiverVariant Variant,
        int Round,
        int Position,
        double LogMicroseconds);
}
