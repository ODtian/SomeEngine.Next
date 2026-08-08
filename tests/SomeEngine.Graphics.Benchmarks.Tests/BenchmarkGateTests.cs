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
            BenchmarkProfile.VendorCertification,
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
    public void FastDiagnosticRecordsFourLatinSquareRoundsAndOnlyDrawWorkloads()
    {
        ProtocolSnapshot snapshot = GateTestData.DiagnosticProtocol;

        Assert.Equal(FixedGraphicsProtocol.DiagnosticProcessCount, snapshot.InterleavedRounds.Length);
        for (int position = 0; position < FixedGraphicsProtocol.Variants.Length; position++)
        {
            Assert.Equal(
                FixedGraphicsProtocol.Variants.Order(),
                snapshot.InterleavedRounds
                    .Select(round => Enum.Parse<ReceiverVariant>(round[position]))
                    .Order());
        }
        Assert.Equal(
            FixedGraphicsProtocol.DiagnosticWorkloads.Select(static value => value.ToString()),
            snapshot.Workloads);
        Assert.DoesNotContain(GraphicsWorkload.EmptySubmit.ToString(), snapshot.Workloads);
        Assert.DoesNotContain(GraphicsWorkload.ExplicitBarrier4096.ToString(), snapshot.Workloads);
        Assert.DoesNotContain(GraphicsWorkload.ThreeQueuePresent.ToString(), snapshot.Workloads);
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
                    BenchmarkProfile.VendorCertification,
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
                    [comparison],
                    []));

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
    public void EvaluateReadsHashedRawEvidenceAndFailsClosedAfterTampering()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"someengine-graphics-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ProcessRun[] runs = GateTestData.ValidWarpRuns();
            var rawEvidence = new RawProcessEvidence[runs.Length];
            for (int position = 0; position < runs.Length; position++)
            {
                ProcessRun run = runs[position];
                string rawPath = Path.Combine(directory, $"00-{position:D2}.json");
                GraphicsBenchmarkReport.WriteProcess(rawPath, run);
                rawEvidence[position] = new RawProcessEvidence(
                    Path.GetFileName(rawPath),
                    BenchmarkEnvironment.Sha256File(rawPath),
                    run.Variant,
                    0,
                    position);
            }

            string reportPath = Path.Combine(directory, "report.json");
            new GraphicsBenchmarkReport(
                FixedGraphicsProtocol.Schema,
                BenchmarkProfile.WarpFunctional,
                RunDisposition.FunctionalOnly,
                "test",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                GateTestData.WarpProtocol,
                rawEvidence,
                new GateResult(RunDisposition.Failed, "stored gate is not trusted", [], [], []))
                .Write(reportPath);
            BenchmarkOptions options = BenchmarkOptions.Parse([
                "evaluate",
                "--input", reportPath,
            ]);

            Assert.Equal(0, BenchmarkController.EvaluateExisting(options));

            File.AppendAllText(Path.Combine(directory, "00-00.json"), " ");
            Assert.Equal(3, BenchmarkController.EvaluateExisting(options));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CompleteFastDiagnosticIsMarkedNonCertificationAndCanNeverPass()
    {
        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.FastDiagnostic,
            GateTestData.DiagnosticProtocol,
            GateTestData.ValidDiagnosticRuns());

        Assert.Equal(RunDisposition.FunctionalOnly, result.Disposition);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "RHI-EVID-DIAGNOSTIC-NONCERTIFICATION");
        Assert.Empty(result.Comparisons);
        Assert.Equal(6, result.Diagnostics.Length);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(4, diagnostic.PositionEffectsPercent.Length);
            Assert.Equal(4, diagnostic.RoundEffectsPercent.Length);
            Assert.Equal(4, diagnostic.VariantEffectsPercent.Length);
            Assert.True(double.IsFinite(diagnostic.PositionSpreadPercent));
            Assert.True(double.IsFinite(diagnostic.RoundSpreadPercent));
            Assert.True(double.IsFinite(diagnostic.VariantSpreadPercent));
            Assert.True(double.IsFinite(diagnostic.ResidualRmsPercent));
        });
    }

    [Fact]
    public void FastDiagnosticWithAnExcludedOrMissingWorkloadFailsClosed()
    {
        ProcessRun[] runs = GateTestData.ValidDiagnosticRuns();
        runs[0] = runs[0] with { Workloads = runs[0].Workloads[1..] };

        GateResult result = BenchmarkGate.Evaluate(
            BenchmarkProfile.FastDiagnostic,
            GateTestData.DiagnosticProtocol,
            runs);

        Assert.Equal(RunDisposition.Failed, result.Disposition);
        Assert.Contains(result.Issues, issue => issue.Code == "RHI-EVID-WORKLOAD-INVENTORY");
        Assert.Contains(result.Issues, issue =>
            issue.Code == "RHI-EVID-DIAGNOSTIC-NONCERTIFICATION");
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
        BenchmarkProfile.WarpFunctional,
        FixedGraphicsProtocol.WarpWarmupFrames,
        FixedGraphicsProtocol.WarpMeasuredFrames,
        1,
        FixedGraphicsProtocol.WarpDrawCount,
        FixedGraphicsProtocol.WarpBarrierCount);

    internal static ProtocolSnapshot DiagnosticProtocol { get; } = ProtocolSnapshot.Create(
        BenchmarkProfile.FastDiagnostic,
        FixedGraphicsProtocol.DiagnosticWarmupFrames,
        FixedGraphicsProtocol.DiagnosticMeasuredFrames,
        FixedGraphicsProtocol.DiagnosticProcessCount,
        FixedGraphicsProtocol.DiagnosticDrawCount,
        FixedGraphicsProtocol.DiagnosticBarrierCount);

    internal static ProcessRun[] ValidWarpRuns() =>
        FixedGraphicsProtocol.Variants.Select(CreateRun).ToArray();

    internal static ProcessRun[] ValidDiagnosticRuns() =>
        Enumerable.Range(0, FixedGraphicsProtocol.DiagnosticProcessCount)
            .SelectMany(processIndex => FixedGraphicsProtocol.Variants.Select(
                variant => CreateDiagnosticRun(variant, processIndex)))
            .ToArray();

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

    private static ProcessRun CreateDiagnosticRun(ReceiverVariant variant, int processIndex)
    {
        WorkloadRun[] workloads = FixedGraphicsProtocol.DiagnosticWorkloads
            .Select(CreateDiagnosticWorkload)
            .ToArray();
        return new ProcessRun(
            variant,
            RunDisposition.FunctionalOnly,
            "diagnostic",
            CreateEnvironment() with
            {
                ProcessIndex = processIndex,
                AdapterName = "Test Hardware Adapter",
                VendorId = 1,
                DeviceId = 2,
                HardwareAccelerated = true,
            },
            workloads);
    }

    private static WorkloadRun CreateDiagnosticWorkload(GraphicsWorkload workload)
    {
        WorkloadRun baseline = CreateWorkload(workload);
        FrameSample[] samples = Enumerable
            .Range(0, FixedGraphicsProtocol.DiagnosticMeasuredFrames)
            .Select(index => new FrameSample(index, 100, 1, 1, 0, 0, checked((ulong)index + 1)))
            .ToArray();
        return baseline with
        {
            Reason = "diagnostic",
            WarmupFrames = FixedGraphicsProtocol.DiagnosticWarmupFrames,
            MeasuredFrames = FixedGraphicsProtocol.DiagnosticMeasuredFrames,
            DrawCount = FixedGraphicsProtocol.DiagnosticDrawCount,
            Samples = samples,
            NativeSetters = baseline.NativeSetters with
            {
                DrawCalls = FixedGraphicsProtocol.DiagnosticDrawCount,
            },
            Cpu = MetricDistribution.From(samples.Select(static value => value.CpuMicroseconds).ToArray()),
            Gpu = MetricDistribution.From(samples.Select(static value => value.GpuMicroseconds!.Value).ToArray()),
        };
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
