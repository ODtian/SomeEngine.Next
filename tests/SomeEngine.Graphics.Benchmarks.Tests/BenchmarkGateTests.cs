namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class BenchmarkGateTests
{
    [Fact]
    public void FixedScheduleBalancesTheFirstFourRoundsAndRecordsTheCanonicalFifth()
    {
        Assert.Equal(FixedGraphicsProtocol.ProcessCount, FixedGraphicsProtocol.InterleavedRounds.Length);
        for (int processIndex = 0; processIndex < FixedGraphicsProtocol.ProcessCount; processIndex++)
        {
            Assert.Equal(
                FixedGraphicsProtocol.Variants.Order(),
                FixedGraphicsProtocol.InterleavedRounds[processIndex].Order());
        }
        for (int position = 0; position < FixedGraphicsProtocol.Variants.Length; position++)
        {
            Assert.Equal(
                FixedGraphicsProtocol.Variants.Order(),
                FixedGraphicsProtocol.InterleavedRounds[..4]
                    .Select(round => round[position])
                    .Order());
        }
        Assert.Equal(
            FixedGraphicsProtocol.Variants,
            FixedGraphicsProtocol.InterleavedRounds[^1]);

        ProtocolSnapshot snapshot = ProtocolSnapshot.Create(
            FixedGraphicsProtocol.WarmupFrames,
            FixedGraphicsProtocol.MeasuredFrames,
            FixedGraphicsProtocol.ProcessCount,
            FixedGraphicsProtocol.DrawCount,
            FixedGraphicsProtocol.BarrierCount);
        Assert.Equal(FixedGraphicsProtocol.ProcessCount, snapshot.InterleavedRounds.Length);
        Assert.Equal(
            FixedGraphicsProtocol.InterleavedRounds[2].Select(static value => value.ToString()),
            snapshot.InterleavedRounds[2]);
    }

    [Fact]
    public void UnrecordedProcessScheduleFailsClosed()
    {
        string[][] wrongSchedule = GateTestData.WarpProtocol.InterleavedRounds
            .Select(static round => round.ToArray())
            .ToArray();
        (wrongSchedule[0][0], wrongSchedule[0][1]) =
            (wrongSchedule[0][1], wrongSchedule[0][0]);

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol with { InterleavedRounds = wrongSchedule },
            GateTestData.ValidWarpRuns());

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-PROTOCOL-ORDER");
    }

    [Fact]
    public void AbsoluteOnlyComparisonSerializesWithoutNonFiniteJsonNumbers()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"someengine-graphics-report-{Guid.NewGuid():N}.json");
        try
        {
            ComparisonResult comparison = new(
                "absolute-only",
                GraphicsWorkload.EmptySubmit,
                "CPU",
                "P50",
                1,
                0,
                1,
                DeltaPercent: null,
                AbsoluteLimitMicroseconds: 1,
                RelativeLimitPercent: null,
                Passed: true);
            GraphicsBenchmarkReport report = new(
                FixedGraphicsProtocol.Schema,
                BenchmarkProfile.VendorCertification,
                RunDisposition.Passed,
                "test",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                ProtocolSnapshot.Create(
                    FixedGraphicsProtocol.WarmupFrames,
                    FixedGraphicsProtocol.MeasuredFrames,
                    FixedGraphicsProtocol.ProcessCount,
                    FixedGraphicsProtocol.DrawCount,
                    FixedGraphicsProtocol.BarrierCount),
                [],
                new GateResult(
                    RunDisposition.Passed,
                    "test",
                    [],
                    [comparison]));

            report.Write(path);
            GraphicsBenchmarkReport roundTripped = GraphicsBenchmarkReport.Read(path);

            ComparisonResult actual = Assert.Single(roundTripped.Gate.Comparisons);
            Assert.Null(actual.DeltaPercent);
            Assert.Null(actual.RelativeLimitPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompleteWarpEvidenceIsFunctionalOnlyAndHasNoPerformanceComparisons()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[0] = GateTestData.WithCpu(runs[0], 1_000_000);
        runs[1] = GateTestData.WithCpu(runs[1], 0.001);
        runs[2] = GateTestData.WithCpu(runs[2], 0.001);
        runs[3] = GateTestData.WithCpu(runs[3], 0.001);

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Equal(RunDisposition.FunctionalOnly, result.Disposition);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Comparisons);
    }

    [Fact]
    public void MissingReceiverFailsClosed()
    {
        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            GateTestData.ValidWarpRuns()[..3]);

        Assert.Equal(RunDisposition.Failed, result.Disposition);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "RHI-EVID-PROCESS-COUNT" && issue.Variant == ReceiverVariant.NativeCpp);
    }

    [Fact]
    public void UnexecutedReceiverNeverCountsAsEvidence()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[3] = runs[3] with
        {
            Disposition = RunDisposition.Unexecuted,
            Reason = "native executable missing",
            Workloads = [],
        };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Equal(RunDisposition.Failed, result.Disposition);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "RHI-EVID-RUN-NOT-EXECUTED" && issue.Variant == ReceiverVariant.NativeCpp);
    }

    [Fact]
    public void AllocationInAnyMeasuredFrameFailsClosed()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        WorkloadRun workload = runs[0].Workloads[1];
        FrameSample[] samples = [.. workload.Samples];
        samples[2] = samples[2] with { ManagedAllocatedBytes = 1 };
        runs[0] = GateTestData.ReplaceWorkload(runs[0], workload with { Samples = samples });

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-ALLOCATION");
    }

    [Fact]
    public void CrossReceiverOutputMismatchFailsClosed()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        WorkloadRun workload = runs[3].Workloads[1];
        runs[3] = GateTestData.ReplaceWorkload(
            runs[3],
            workload with { OutputSha256 = "DIFFERENT" });

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-OUTPUT-HASH");
    }

    [Fact]
    public void MissingNonEmptyGpuSampleFailsClosed()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        WorkloadRun workload = runs[2].Workloads[1];
        FrameSample[] samples = [.. workload.Samples];
        samples[0] = samples[0] with { GpuMicroseconds = null };
        runs[2] = GateTestData.ReplaceWorkload(runs[2], workload with { Samples = samples });

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-GPU-SAMPLES");
    }

    [Fact]
    public void StateSuppressionMustRetainDrawsAndSuppressNativeSetters()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        WorkloadRun workload = runs[1].Workloads.Single(value =>
            value.Workload == GraphicsWorkload.StateSuppression10000);
        runs[1] = GateTestData.ReplaceWorkload(
            runs[1],
            workload with
            {
                NativeSetters = workload.NativeSetters with { PipelineSetters = 2 },
            });

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-STATE-SUPPRESSION");
    }

    [Fact]
    public void ExplicitBarrierOrdinalsMustBeContiguous()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        WorkloadRun workload = runs[0].Workloads.Single(value =>
            value.Workload == GraphicsWorkload.ExplicitBarrier4096);
        BarrierEvidence[] barriers = [.. workload.Barriers];
        barriers[5] = barriers[5] with { PublicOrdinal = 9 };
        runs[0] = GateTestData.ReplaceWorkload(
            runs[0],
            workload with { Barriers = barriers });

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-BARRIER-ORDINAL");
    }

    [Fact]
    public void VendorCertificationRejectsReducedProtocolAndSoftwareAdapter()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns()
            .Select(run => run with { Disposition = RunDisposition.Passed })
            .ToArray();

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.VendorCertification,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-PROTOCOL-SHAPE");
        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-WARP-NOT-PERFORMANCE");
        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-NOT-RELEASE");
    }

    [Fact]
    public void RequiredProcessIndexCannotBeReplacedByAnotherIndex()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[0] = runs[0] with
        {
            Environment = runs[0].Environment with { ProcessIndex = 1 },
        };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-PROCESS-INDEX");
    }

    [Fact]
    public void CpuOrPowerDriftFailsEnvironmentCohort()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[2] = runs[2] with
        {
            Environment = runs[2].Environment with { ProcessorName = "Different CPU" },
        };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-ENVIRONMENT-COHORT");
    }

    [Fact]
    public void ManagedPayloadDriftFailsBuildCohort()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[1] = runs[1] with
        {
            Environment = runs[1].Environment with
            {
                Build = runs[1].Environment.Build with { PayloadSha256 = "different" },
            },
        };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-BUILD-COHORT");
    }

    [Fact]
    public void MissingDriverIdentityFailsClosed()
    {
        ProcessRun[] runs = GateTestData.ValidWarpRuns();
        runs[3] = runs[3] with
        {
            Environment = runs[3].Environment with { DriverVersion = "unavailable" },
        };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.WarpFunctional,
            GateTestData.WarpProtocol,
            runs);

        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-ENVIRONMENT-INCOMPLETE");
    }
}

internal static class GateTestData
{
    internal static ProtocolSnapshot WarpProtocol { get; } = ProtocolSnapshot.Create(
        FixedGraphicsProtocol.WarpWarmupFrames,
        FixedGraphicsProtocol.WarpMeasuredFrames,
        1,
        FixedGraphicsProtocol.WarpDrawCount,
        FixedGraphicsProtocol.WarpBarrierCount);

    internal static ProcessRun[] ValidWarpRuns() =>
        FixedGraphicsProtocol.Variants.Select(CreateRun).ToArray();

    internal static ProcessRun WithCpu(ProcessRun run, double microseconds)
    {
        WorkloadRun[] workloads = run.Workloads.Select(workload =>
        {
            FrameSample[] samples = workload.Samples
                .Select(sample => sample with { CpuMicroseconds = microseconds })
                .ToArray();
            return workload with { Samples = samples };
        }).ToArray();
        return run with { Workloads = workloads };
    }

    internal static ProcessRun ReplaceWorkload(ProcessRun run, WorkloadRun replacement)
    {
        WorkloadRun[] workloads = [.. run.Workloads];
        int index = Array.FindIndex(workloads, value => value.Workload == replacement.Workload);
        workloads[index] = replacement;
        return run with { Workloads = workloads };
    }

    private static ProcessRun CreateRun(ReceiverVariant variant)
    {
        WorkloadRun[] workloads = FixedGraphicsProtocol.Workloads
            .Select(CreateWorkload)
            .ToArray();
        return new ProcessRun(
            variant,
            RunDisposition.FunctionalOnly,
            "functional",
            CreateEnvironment(),
            workloads);
    }

    private static WorkloadRun CreateWorkload(GraphicsWorkload workload)
    {
        FrameSample[] samples = Enumerable.Range(0, FixedGraphicsProtocol.WarpMeasuredFrames)
            .Select(index => new FrameSample(
                index,
                100,
                1,
                workload == GraphicsWorkload.EmptySubmit ? null : 1,
                0,
                0,
                checked((ulong)index + 1)))
            .ToArray();
        int drawCount = workload switch
        {
            GraphicsWorkload.PersistentDraw10000 or
            GraphicsWorkload.TransientDraw10000 or
            GraphicsWorkload.StateSuppression10000 => FixedGraphicsProtocol.WarpDrawCount,
            GraphicsWorkload.ThreeQueuePresent => 1,
            _ => 0,
        };
        BarrierEvidence[] barriers = workload switch
        {
            GraphicsWorkload.ExplicitBarrier4096 => Enumerable
                .Range(0, FixedGraphicsProtocol.WarpBarrierCount)
                .Select(index => new BarrierEvidence(index, "MemoryBarrier", index, 1, null))
                .ToArray(),
            GraphicsWorkload.PersistentDraw10000 or
            GraphicsWorkload.TransientDraw10000 or
            GraphicsWorkload.StateSuppression10000 =>
            [
                new(0, "TextureBarrier", 0, 1, null),
                new(1, "TextureBarrier", 1, 1, null),
            ],
            GraphicsWorkload.ThreeQueuePresent => Enumerable.Range(0, 10)
                .Select(index => new BarrierEvidence(index, "Barrier", index, 1, null))
                .ToArray(),
            _ => [],
        };
        NativeSetterEvidence setters = workload switch
        {
            GraphicsWorkload.PersistentDraw10000 => new(1, 1, 1, 1, drawCount),
            GraphicsWorkload.TransientDraw10000 => new(1, 0, 1, 1, drawCount),
            GraphicsWorkload.StateSuppression10000 => new(1, 1, 1, 1, drawCount),
            GraphicsWorkload.ExplicitBarrier4096 => new(1, 0, 0, 0, 0),
            GraphicsWorkload.ThreeQueuePresent => new(1, 1, 1, 1, 1),
            _ => default,
        };
        return new WorkloadRun(
            workload,
            RunDisposition.FunctionalOnly,
            "functional",
            FixedGraphicsProtocol.WarpWarmupFrames,
            FixedGraphicsProtocol.WarpMeasuredFrames,
            drawCount,
            barriers.Length,
            samples,
            [],
            $"hash-{workload}",
            "shared-shader",
            barriers,
            setters,
            MetricDistribution.From(samples.Select(static sample => sample.CpuMicroseconds).ToArray()),
            workload == GraphicsWorkload.EmptySubmit
                ? null
                : MetricDistribution.From(samples.Select(static sample => sample.GpuMicroseconds!.Value).ToArray()));
    }

    private static RuntimeEnvironment CreateEnvironment() => new(
        "Windows",
        "X64",
        "Test CPU",
        1,
        0,
        1,
        "High",
        "test",
        "WARP",
        0,
        0,
        1,
        0,
        "test",
        false,
        619,
        false,
        false,
        false,
        new BuildIdentity(
            "test.exe",
            "sha",
            "payload-sha",
            "1.0",
            "Debug",
            "commit",
            false,
            "test"));
}
