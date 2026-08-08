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

    private static readonly Limits GenericVsSilk = new(50, 3, 100, 5, 200, 8);
    private static readonly Limits GenericVsCpp = new(100, 5, 200, 8, 300, 10);
    private static readonly Limits InterfaceVsGeneric = new(25, 3, 50, 5, 100, 8);
    private static readonly Limits GpuLimits = new(20, 1, 50, 2, 100, 3);
    private static readonly Limits EmptyGenericVsSilk = new(1, null, 2, null, 4, null);
    private static readonly Limits EmptyGenericVsCpp = new(2, null, 3, null, 6, null);
    private static readonly Limits EmptyInterfaceVsGeneric = new(0.5, null, 1, null, 2, null);

    internal static GateResult Evaluate(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ReadOnlySpan<ProcessRun> runs)
    {
        var issues = new List<GateIssue>();
        var comparisons = new List<ComparisonResult>();

        ValidateRunInventory(profile, protocol, runs, issues);
        ValidateEnvironmentCohort(profile, runs, issues);
        foreach (ProcessRun run in runs)
            ValidateRun(profile, protocol, run, issues);

        foreach (GraphicsWorkload workload in FixedGraphicsProtocol.Workloads)
        {
            WorkloadAggregate? generic = Aggregate(runs, ReceiverVariant.GenericRhi, workload, issues);
            WorkloadAggregate? throughInterface = Aggregate(runs, ReceiverVariant.InterfaceRhi, workload, issues);
            WorkloadAggregate? silk = Aggregate(runs, ReceiverVariant.DirectSilk, workload, issues);
            WorkloadAggregate? cpp = Aggregate(runs, ReceiverVariant.NativeCpp, workload, issues);
            if (generic is null || throughInterface is null || silk is null || cpp is null)
                continue;

            RequireEquivalentOutput(workload, generic, throughInterface, silk, cpp, issues);
            RequireRhiEvidenceIdentity(workload, generic, throughInterface, issues);
            if (profile != BenchmarkProfile.VendorCertification)
                continue;

            CompareCpu(
                "Generic RHI vs Direct Silk",
                workload,
                generic.Cpu,
                silk.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptyGenericVsSilk : GenericVsSilk,
                comparisons,
                issues);
            CompareCpu(
                "Generic RHI vs C++ D3D12",
                workload,
                generic.Cpu,
                cpp.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptyGenericVsCpp : GenericVsCpp,
                comparisons,
                issues);
            CompareCpu(
                "Interface RHI vs Generic RHI",
                workload,
                throughInterface.Cpu,
                generic.Cpu,
                workload == GraphicsWorkload.EmptySubmit ? EmptyInterfaceVsGeneric : InterfaceVsGeneric,
                comparisons,
                issues);

            if (workload != GraphicsWorkload.EmptySubmit)
            {
                if (generic.Gpu is null || silk.Gpu is null || cpp.Gpu is null)
                {
                    issues.Add(new GateIssue(
                        "RHI-EVID-GPU-MISSING",
                        "A non-empty workload is missing raw GPU timestamps.",
                        Workload: workload));
                }
                else
                {
                    CompareDistribution(
                        "Generic RHI GPU vs Direct Silk",
                        workload,
                        "GPU",
                        generic.Gpu.Value,
                        silk.Gpu.Value,
                        GpuLimits,
                        comparisons,
                        issues);
                    CompareDistribution(
                        "Generic RHI GPU vs C++ D3D12",
                        workload,
                        "GPU",
                        generic.Gpu.Value,
                        cpp.Gpu.Value,
                        GpuLimits,
                        comparisons,
                        issues);
                }
            }
        }

        if (profile == BenchmarkProfile.WarpFunctional)
        {
            return new GateResult(
                issues.Count == 0 ? RunDisposition.FunctionalOnly : RunDisposition.Failed,
                issues.Count == 0
                    ? "All four receivers are functionally equivalent on WARP; WARP is not vendor performance evidence."
                    : "WARP functional equivalence failed.",
                [.. issues],
                [.. comparisons]);
        }

        return new GateResult(
            issues.Count == 0 ? RunDisposition.Passed : RunDisposition.Failed,
            issues.Count == 0
                ? "The fixed vendor-hardware performance protocol passed."
                : "The fixed vendor-hardware performance protocol did not pass.",
            [.. issues],
            [.. comparisons]);
    }

    private static void ValidateRunInventory(
        BenchmarkProfile profile,
        ProtocolSnapshot protocol,
        ReadOnlySpan<ProcessRun> runs,
        List<GateIssue> issues)
    {
        int expectedProcesses = profile == BenchmarkProfile.VendorCertification
            ? FixedGraphicsProtocol.ProcessCount
            : 1;
        foreach (ReceiverVariant variant in FixedGraphicsProtocol.Variants)
        {
            int count = 0;
            var processIndices = new HashSet<int>();
            foreach (ProcessRun run in runs)
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

        if (protocol.ProcessCount != expectedProcesses ||
            !HasExactInterleavedSchedule(protocol.InterleavedRounds, expectedProcesses))
        {
            issues.Add(new GateIssue(
                "RHI-EVID-PROTOCOL-ORDER",
                "The report does not record the fixed deterministic interleaved process schedule."));
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
    }

    private static void ValidateEnvironmentCohort(
        BenchmarkProfile profile,
        ReadOnlySpan<ProcessRun> runs,
        List<GateIssue> issues)
    {
        ProcessRun[] executed = runs.ToArray()
            .Where(static run => run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .ToArray();
        if (executed.Length == 0)
            return;

        RuntimeEnvironment reference = executed[0].Environment;
        foreach (ProcessRun run in executed)
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

    private static void ValidateVariantBuild(
        ProcessRun[] runs,
        ReceiverVariant variant,
        List<GateIssue> issues)
    {
        ProcessRun[] family = runs
            .Where(run => run.Variant == variant)
            .ToArray();
        if (family.Length == 0)
            return;

        BuildIdentity reference = family[0].Environment.Build;
        foreach (ProcessRun run in family)
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

    private static void ValidateManagedExecutable(ProcessRun[] runs, List<GateIssue> issues)
    {
        ProcessRun[] managed = runs
            .Where(static run => run.Variant != ReceiverVariant.NativeCpp)
            .ToArray();
        if (managed.Length == 0)
            return;

        BuildIdentity reference = managed[0].Environment.Build;
        foreach (ProcessRun run in managed)
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
        ProcessRun run,
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

        if (run.Workloads.Length != FixedGraphicsProtocol.Workloads.Length)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-WORKLOAD-INVENTORY",
                $"{run.Variant} did not report all fixed workloads.",
                run.Variant));
        }
        foreach (GraphicsWorkload workload in FixedGraphicsProtocol.Workloads)
        {
            WorkloadRun? result = run.Workloads.SingleOrDefault(value => value.Workload == workload);
            if (result is null)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-WORKLOAD-MISSING",
                    $"{run.Variant} omitted {workload}.",
                    run.Variant,
                    workload));
                continue;
            }
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
                result.Samples.Length != protocol.MeasuredFrames)
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-SAMPLE-COUNT",
                    $"{run.Variant}/{workload} has the wrong warm-up or measured sample count.",
                    run.Variant,
                    workload));
            }
            foreach (FrameSample sample in result.Samples)
            {
                if (sample.ManagedAllocatedBytes != 0 || sample.EtwAllocationEvents != 0)
                {
                    issues.Add(new GateIssue(
                        "RHI-EVID-ALLOCATION",
                        $"{run.Variant}/{workload} frame {sample.FrameIndex} allocated " +
                        $"{sample.ManagedAllocatedBytes} B and observed {sample.EtwAllocationEvents} allocation event(s).",
                        run.Variant,
                        workload));
                    break;
                }
            }
            if (workload != GraphicsWorkload.EmptySubmit &&
                result.Samples.Any(static sample => sample.GpuMicroseconds is null))
            {
                issues.Add(new GateIssue(
                    "RHI-EVID-GPU-SAMPLES",
                    $"{run.Variant}/{workload} is missing one or more raw GPU samples.",
                    run.Variant,
                    workload));
            }
            if (workload == GraphicsWorkload.StateSuppression10000)
            {
                NativeSetterEvidence setters = result.NativeSetters;
                if (setters.PipelineSetters > 1 || setters.PersistentBindingSetters > 1 ||
                    setters.ViewportSetters > 1 || setters.ScissorSetters > 1 ||
                    setters.DrawCalls != result.DrawCount)
                {
                    issues.Add(new GateIssue(
                        "RHI-EVID-STATE-SUPPRESSION",
                        $"{run.Variant} did not preserve 10,000 draws while suppressing equal native setters.",
                        run.Variant,
                        workload));
                }
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

    private static WorkloadAggregate? Aggregate(
        ReadOnlySpan<ProcessRun> runs,
        ReceiverVariant variant,
        GraphicsWorkload workload,
        List<GateIssue> issues)
    {
        WorkloadRun[] selected = runs.ToArray()
            .Where(run => run.Variant == variant && run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .SelectMany(static run => run.Workloads)
            .Where(run => run.Workload == workload && run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly)
            .ToArray();
        if (selected.Length == 0)
            return null;

        double[] cpu = selected.SelectMany(static run => run.Samples)
            .Select(static sample => sample.CpuMicroseconds)
            .ToArray();
        double[] gpu = selected.SelectMany(static run => run.Samples)
            .Where(static sample => sample.GpuMicroseconds.HasValue)
            .Select(static sample => sample.GpuMicroseconds!.Value)
            .ToArray();
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
            selected[0].Barriers,
            selected[0].NativeSetters);
    }

    private static void RequireEquivalentOutput(
        GraphicsWorkload workload,
        WorkloadAggregate generic,
        WorkloadAggregate throughInterface,
        WorkloadAggregate silk,
        WorkloadAggregate cpp,
        List<GateIssue> issues)
    {
        string[] hashes = [generic.OutputSha256, throughInterface.OutputSha256, silk.OutputSha256, cpp.OutputSha256];
        string[] shaders = [generic.ShaderManifestSha256, throughInterface.ShaderManifestSha256, silk.ShaderManifestSha256, cpp.ShaderManifestSha256];
        if (hashes.Any(string.IsNullOrWhiteSpace) || hashes.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-OUTPUT-HASH",
                "The four receivers did not produce one exact output hash.",
                Workload: workload));
        }
        if (shaders.Any(string.IsNullOrWhiteSpace) || shaders.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-SHADER-IDENTITY",
                "The four receivers did not consume one Slang-produced DXIL manifest.",
                Workload: workload));
        }
    }

    private static void RequireRhiEvidenceIdentity(
        GraphicsWorkload workload,
        WorkloadAggregate generic,
        WorkloadAggregate throughInterface,
        List<GateIssue> issues)
    {
        if (!generic.Barriers.SequenceEqual(throughInterface.Barriers) ||
            generic.NativeSetters != throughInterface.NativeSetters)
        {
            issues.Add(new GateIssue(
                "RHI-EVID-RHI-NATIVE-IDENTITY",
                "Generic and interface RHI did not report identical barrier/native-state evidence.",
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
        BarrierEvidence[] Barriers,
        NativeSetterEvidence NativeSetters);
}
