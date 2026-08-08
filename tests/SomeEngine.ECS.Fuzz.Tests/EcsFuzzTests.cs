using System.Diagnostics;
using System.Globalization;
using Xunit.Sdk;

namespace SomeEngine.ECS.Fuzz.Tests;

public sealed class EcsFuzzTests
{
    public static TheoryData<ulong> SeedBank =>
    [
        0x0000000000000001UL,
        0x243F6A8885A308D3UL,
        0x9E3779B97F4A7C15UL,
        0xD1B54A32D192ED03UL,
        0xF00DFACECAFEBEEFUL,
        0xFFFFFFFFFFFFFFFFUL,
    ];

    [Theory]
    [MemberData(nameof(SeedBank))]
    public void FixedSeedBank_MatchesDictionaryReferenceModel(ulong seed)
    {
        EcsFuzzTrace trace = EcsFuzzTraceGenerator.Generate(seed, stepCount: 160);

        FuzzRunResult result = RunWithFailureArtifact(trace);

        Assert.Equal(160, result.StepCount);
        Assert.True(result.SuccessfulBatches > 0);
        Assert.True(result.RejectedBatches >= 5);
        Assert.False(string.IsNullOrWhiteSpace(result.StateDigest));
    }

    [Fact]
    public void FixedPrng_MatchesGoldenVector()
    {
        var random = new FixedPrng(1);
        ulong[] actual = Enumerable.Range(0, 6)
            .Select(_ => random.NextUInt64())
            .ToArray();

        Assert.Equal(
            [
                0x47E4CE4B896CDD1DUL,
                0xABCFA6A8E079651DUL,
                0xB9D10D8FEB731F57UL,
                0x4DB418A0BB1B019DUL,
                0x0E6199B04D5AA600UL,
                0xC8674BCB42E3AAD9UL,
            ],
            actual);
    }

    [Fact]
    public void RetainedCoverageTrace_ContainsEveryCommandKind()
    {
        EcsFuzzTrace trace = EcsFuzzTraceGenerator.Generate(
            0x8F0D3C7A52B941E1UL,
            stepCount: 4_096);
        HashSet<FuzzCommandKind> observed = trace.Steps
            .SelectMany(static step => step.Commands)
            .Select(static command => command.Kind)
            .ToHashSet();

        Assert.Equal(
            Enum.GetValues<FuzzCommandKind>().OrderBy(static kind => kind),
            observed.OrderBy(static kind => kind));
    }

    [Fact]
    public void EnvironmentCampaign_ReplaysRequestedSeedWhenConfigured()
    {
        string? seedText = Environment.GetEnvironmentVariable("SOMEENGINE_ECS_FUZZ_SEED");
        string? stepsText = Environment.GetEnvironmentVariable("SOMEENGINE_ECS_FUZZ_STEPS");
        string? evidencePath = Environment.GetEnvironmentVariable("SOMEENGINE_ECS_FUZZ_EVIDENCE");
        string? commitSha = Environment.GetEnvironmentVariable("SOMEENGINE_ECS_FUZZ_COMMIT_SHA");
        if (seedText is null && stepsText is null)
        {
            Assert.True(
                evidencePath is null && commitSha is null,
                "Long-fuzz evidence variables cannot be supplied without seed and steps.");
            return;
        }

        Assert.False(
            string.IsNullOrWhiteSpace(seedText) || string.IsNullOrWhiteSpace(stepsText),
            "SOMEENGINE_ECS_FUZZ_SEED and SOMEENGINE_ECS_FUZZ_STEPS must be supplied together.");
        bool hasEvidencePath = !string.IsNullOrWhiteSpace(evidencePath);
        bool hasCommitSha = !string.IsNullOrWhiteSpace(commitSha);
        Assert.True(
            hasEvidencePath == hasCommitSha,
            "SOMEENGINE_ECS_FUZZ_EVIDENCE and SOMEENGINE_ECS_FUZZ_COMMIT_SHA must be supplied together.");

        string normalizedSeed = seedText!.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? seedText[2..]
            : seedText;
        NumberStyles seedStyle = seedText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? NumberStyles.AllowHexSpecifier
            : NumberStyles.None;
        Assert.True(
            ulong.TryParse(normalizedSeed, seedStyle, CultureInfo.InvariantCulture, out ulong seed),
            $"Invalid SOMEENGINE_ECS_FUZZ_SEED '{seedText}'. Use decimal or a 0x-prefixed hexadecimal value.");
        Assert.True(
            int.TryParse(stepsText, NumberStyles.None, CultureInfo.InvariantCulture, out int steps) &&
            steps is >= 1 and <= 1_000_000,
            "SOMEENGINE_ECS_FUZZ_STEPS must be between 1 and 1,000,000.");

        long started = Stopwatch.GetTimestamp();
        FuzzRunResult result = RunLongCampaignWithFailureArtifact(seed, steps);
        TimeSpan duration = Stopwatch.GetElapsedTime(started);

        Assert.Equal(steps, result.StepCount);
        if (hasEvidencePath)
        {
            LongFuzzEvidence.Write(
                evidencePath!,
                commitSha!,
                EcsFuzzTrace.FixedPrngAlgorithm,
                seed,
                steps,
                result,
                duration);
        }
    }

    [Fact]
    public void StreamingCampaign_MatchesMaterializedTraceWithoutRetainingEveryStep()
    {
        const ulong seed = 0x9E3779B97F4A7C15UL;
        const int steps = 512;
        EcsFuzzTrace trace = EcsFuzzTraceGenerator.Generate(seed, steps);

        FuzzRunResult materialized = new EcsFuzzRunner().Run(trace);
        FuzzRunResult streaming = new EcsFuzzRunner().RunGenerated(
            seed,
            steps,
            fullVerificationInterval: 17);

        Assert.Equal(materialized, streaming);
    }

    [Fact]
    public void EnvironmentTrace_ReplaysExactMinimizedArtifactWhenConfigured()
    {
        string? path = Environment.GetEnvironmentVariable("SOMEENGINE_ECS_FUZZ_TRACE");
        if (string.IsNullOrWhiteSpace(path))
            return;

        EcsFuzzTrace trace = FuzzFailureArtifacts.LoadReplayTrace(path);
        FuzzRunResult result = RunWithFailureArtifact(trace);

        Assert.Equal(trace.Steps.Length, result.StepCount);
    }

    [Fact]
    public void CoreEntityComponentAndCommandBufferOperations_ReplayAgainstReferenceModel()
    {
        EcsFuzzTrace trace = EcsFuzzTrace.Create(
            0x434F52452D4F5053UL,
            Immediate(FuzzCommandKind.CreateEntity, 1),
            Immediate(FuzzCommandKind.AddAlpha, 1, 10),
            Immediate(FuzzCommandKind.ReplaceAlpha, 1, 11),
            Immediate(FuzzCommandKind.AddBeta, 1, 20),
            Immediate(FuzzCommandKind.AddTag, 1),
            Immediate(FuzzCommandKind.RemoveAlpha, 1),
            Batch(
                Command(FuzzCommandKind.CreateEntity, 2),
                Command(FuzzCommandKind.AddAlpha, 2, 30),
                Command(FuzzCommandKind.AddBeta, 2, 40),
                Command(FuzzCommandKind.AddTag, 2),
                Command(FuzzCommandKind.ReplaceBeta, 2, 41)),
            Immediate(FuzzCommandKind.RemoveTag, 1),
            Immediate(FuzzCommandKind.RemoveBeta, 1),
            Immediate(FuzzCommandKind.DestroyEntity, 1),
            Batch(
                Command(FuzzCommandKind.ReplaceAlpha, 2, 31),
                Command(FuzzCommandKind.RemoveBeta, 2),
                Command(FuzzCommandKind.RemoveTag, 2)),
            Immediate(FuzzCommandKind.DestroyEntity, 2));

        FuzzRunResult result = RunWithFailureArtifact(trace);

        Assert.Equal(2, result.SuccessfulBatches);
        Assert.Equal(0, result.RejectedBatches);
        Assert.Equal(0, result.RejectedImmediateOperations);
    }

    [Fact]
    public void StorageAndTopologyOwners_ReplayAgainstIndependentOracleState()
    {
        EcsFuzzTrace trace = EcsFuzzTrace.Create(
            0x4F574E4552532D32UL,
            Immediate(FuzzCommandKind.CreateEntity, 1),
            Immediate(FuzzCommandKind.CreateEntity, 2),
            Immediate(FuzzCommandKind.CreateEntity, 3),
            Batch(
                Command(FuzzCommandKind.AddEnableable, 1, 10),
                Command(FuzzCommandKind.AddIndexed, 1, 100),
                Command(FuzzCommandKind.AddIndexed, 2, 100)),
            Immediate(FuzzCommandKind.Disable, 1),
            Immediate(FuzzCommandKind.ReplaceEnableable, 1, 11),
            Immediate(FuzzCommandKind.Enable, 1),
            Immediate(FuzzCommandKind.AddSparse, 1, 20),
            Immediate(FuzzCommandKind.ReplaceSparse, 1, 21),
            Immediate(FuzzCommandKind.AddShared, 1, 30),
            Immediate(FuzzCommandKind.ReplaceShared, 1, 31),
            Immediate(FuzzCommandKind.AddBuffer, 1),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 1),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 2),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 3),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 4),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 5),
            Immediate(FuzzCommandKind.AppendBuffer, 1, 6),
            Immediate(FuzzCommandKind.SetBufferFirst, 1, 99),
            Immediate(FuzzCommandKind.SetParent, 2, otherEntityId: 1),
            Immediate(FuzzCommandKind.SetParent, 3, otherEntityId: 2),
            Immediate(FuzzCommandKind.SetParent, 3, otherEntityId: 1),
            Immediate(FuzzCommandKind.CreateRelation, 1, 41, otherEntityId: 2),
            Immediate(FuzzCommandKind.CreateRelation, 1, 42, otherEntityId: 3),
            Immediate(FuzzCommandKind.DestroyRelation, 1, otherEntityId: 2),
            Immediate(FuzzCommandKind.Detach, 2),
            Immediate(FuzzCommandKind.RemoveSparse, 1),
            Immediate(FuzzCommandKind.RemoveShared, 1),
            Immediate(FuzzCommandKind.RemoveBuffer, 1),
            Batch(
                Command(FuzzCommandKind.ReplaceIndexed, 1, 101),
                Command(FuzzCommandKind.RemoveIndexed, 2),
                Command(FuzzCommandKind.RemoveEnableable, 1)));

        FuzzRunResult result = RunWithFailureArtifact(trace);

        Assert.Equal(2, result.SuccessfulBatches);
        Assert.Equal(0, result.RejectedBatches);
        Assert.Equal(0, result.RejectedImmediateOperations);
        Assert.Contains("relations=1>3=42", result.StateDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleOperationsAndFailedBatch_RollBackStateAndPublicationEpoch()
    {
        EcsFuzzTrace trace = EcsFuzzTrace.Create(
            0x5354414C452D5242UL,
            Immediate(FuzzCommandKind.CreateEntity, 1),
            Immediate(FuzzCommandKind.AddAlpha, 1, 10),
            Immediate(FuzzCommandKind.CreateEntity, 2),
            Immediate(FuzzCommandKind.DestroyEntity, 2),
            Immediate(FuzzCommandKind.AddAlpha, 2, 99),
            Batch(
                Command(FuzzCommandKind.ReplaceAlpha, 1, 20),
                Command(FuzzCommandKind.CreateEntity, 3),
                Command(FuzzCommandKind.AddBeta, 3, 30),
                Command(FuzzCommandKind.DestroyEntity, 2)),
            Immediate(FuzzCommandKind.ReplaceAlpha, 1, 12));

        FuzzRunResult result = RunWithFailureArtifact(trace);

        Assert.Equal(1, result.RejectedImmediateOperations);
        Assert.Equal(1, result.RejectedBatches);
        Assert.Contains("1:1:12", result.StateDigest, StringComparison.Ordinal);
        Assert.DoesNotContain("3=", result.StateDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBatch_RestoresAllocatorIdentityForSubsequentCreates()
    {
        FuzzStep[] prefix =
        [
            Immediate(FuzzCommandKind.CreateEntity, 1),
            Immediate(FuzzCommandKind.CreateEntity, 2),
            Immediate(FuzzCommandKind.DestroyEntity, 1),
        ];
        FuzzStep[] suffix =
        [
            Immediate(FuzzCommandKind.CreateEntity, 4),
            Immediate(FuzzCommandKind.CreateEntity, 5),
            Immediate(FuzzCommandKind.AddAlpha, 4, 40),
            Immediate(FuzzCommandKind.AddBeta, 5, 50),
        ];
        FuzzStep failedAllocatorMutation = Batch(
            Command(FuzzCommandKind.CreateEntity, 3),
            Command(FuzzCommandKind.AddTag, 3),
            Command(FuzzCommandKind.DestroyEntity, 2),
            Command(FuzzCommandKind.ReplaceAlpha, 2, 999));

        EcsFuzzTrace control = EcsFuzzTrace.Create(
            0x414C4C4F432D4354UL,
            [.. prefix, .. suffix]);
        EcsFuzzTrace withFailure = EcsFuzzTrace.Create(
            0x414C4C4F432D5242UL,
            [.. prefix, failedAllocatorMutation, .. suffix]);

        FuzzRunResult expected = RunWithFailureArtifact(control);
        FuzzRunResult actual = RunWithFailureArtifact(withFailure);

        Assert.Equal(1, actual.RejectedBatches);
        Assert.Equal(expected.StateDigest, actual.StateDigest);
    }

    [Fact]
    public void TraceJson_RoundTripsAndReplaysDeterministically()
    {
        EcsFuzzTrace original = EcsFuzzTraceGenerator.Generate(
            0xA4093822299F31D0UL,
            stepCount: 96);
        string json = original.ToJson();

        EcsFuzzTrace replay = EcsFuzzTrace.FromJson(json);
        FuzzRunResult first = RunWithFailureArtifact(original);
        FuzzRunResult second = RunWithFailureArtifact(replay);

        Assert.Equal(json, replay.ToJson());
        Assert.Equal(first, second);
    }

    [Fact]
    public void FailureArtifactLoader_SelectsMinimizedTraceForReplay()
    {
        EcsFuzzTrace original = EcsFuzzTrace.Create(
            11,
            Immediate(FuzzCommandKind.CreateEntity, 1),
            Immediate(FuzzCommandKind.AddAlpha, 1, 12));
        EcsFuzzTrace minimized = EcsFuzzTrace.Create(
            11,
            Immediate(FuzzCommandKind.CreateEntity, 1));
        string directory = Path.Combine(Path.GetTempPath(), $"ecs-fuzz-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "failure.json");
        try
        {
            var artifact = new FuzzFailureArtifact(
                1,
                DateTimeOffset.UtcNow,
                typeof(InvalidOperationException).FullName!,
                "fingerprint",
                "message",
                null,
                original,
                minimized);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                artifact,
                EcsFuzzTrace.JsonOptions));

            EcsFuzzTrace replay = FuzzFailureArtifacts.LoadReplayTrace(path);

            Assert.Equal(minimized.ToJson(), replay.ToJson());
            Assert.Equal(1, new EcsFuzzRunner().Run(replay).StepCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LongCampaignEvidence_IsAtomicAndExplicitlyDistinctFromOrdinaryTests()
    {
        EcsFuzzTrace trace = EcsFuzzTrace.Create(
            0x1234,
            Immediate(FuzzCommandKind.CreateEntity, 1));
        FuzzRunResult result = new EcsFuzzRunner().Run(trace);
        string directory = Path.Combine(Path.GetTempPath(), $"ecs-fuzz-evidence-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "campaign.json");
        try
        {
            LongFuzzEvidence.Write(
                path,
                new string('a', 40),
                trace,
                result,
                TimeSpan.FromSeconds(1));

            LongFuzzCampaignEvidence evidence =
                System.Text.Json.JsonSerializer.Deserialize<LongFuzzCampaignEvidence>(
                    File.ReadAllText(path),
                    EcsFuzzTrace.JsonOptions)
                ?? throw new InvalidDataException("Long-fuzz evidence was empty.");
            Assert.True(evidence.Passed);
            Assert.True(evidence.Clean);
            Assert.Equal(1, evidence.Steps);
            Assert.Equal("0x0000000000001234", evidence.Seed);
            Assert.Equal(EcsFuzzTraceGenerator.MaximumLogicalEntities, evidence.MaximumLogicalEntities);
            Assert.Equal(
                EcsFuzzRunner.LongCampaignFullVerificationInterval,
                evidence.FullVerificationInterval);
            Assert.Matches("^[0-9a-f]{64}$", evidence.StateDigest);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp.*"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeltaDebugger_RemovesIrrelevantTraceSteps()
    {
        EcsFuzzTrace trace = EcsFuzzTrace.Create(
            7,
            Immediate(FuzzCommandKind.AddAlpha, 1, 1),
            Immediate(FuzzCommandKind.AddAlpha, 1, 2),
            Immediate(FuzzCommandKind.AddAlpha, 1, 999),
            Immediate(FuzzCommandKind.AddAlpha, 1, 3),
            Immediate(FuzzCommandKind.AddAlpha, 1, 4));

        EcsFuzzTrace minimized = FuzzDeltaDebugger.Minimize(
            trace,
            static candidate => candidate.Steps
                .SelectMany(static step => step.Commands)
                .Any(static command => command.Value == 999));

        FuzzStep remaining = Assert.Single(minimized.Steps);
        Assert.Equal(999, Assert.Single(remaining.Commands).Value);
        Assert.Equal(trace.Seed, minimized.Seed);
        Assert.Equal(trace.PrngAlgorithm, minimized.PrngAlgorithm);
    }

    private static FuzzRunResult RunWithFailureArtifact(EcsFuzzTrace trace)
    {
        try
        {
            return new EcsFuzzRunner().Run(trace);
        }
        catch (Exception failure)
        {
            string fingerprint = FuzzFailureArtifacts.Fingerprint(failure);
            EcsFuzzTrace minimized = FuzzDeltaDebugger.Minimize(
                trace,
                candidate =>
                {
                    Exception? candidateFailure = FuzzFailureArtifacts.ReplayFailure(candidate);
                    return candidateFailure is not null &&
                           string.Equals(
                               FuzzFailureArtifacts.Fingerprint(candidateFailure),
                               fingerprint,
                               StringComparison.Ordinal);
                });

            Exception? minimizedFailure = FuzzFailureArtifacts.ReplayFailure(minimized);
            if (minimizedFailure is null ||
                !string.Equals(
                    FuzzFailureArtifacts.Fingerprint(minimizedFailure),
                    fingerprint,
                    StringComparison.Ordinal))
            {
                minimized = trace;
            }

            string artifactPath;
            try
            {
                artifactPath = FuzzFailureArtifacts.Write(trace, minimized, failure);
            }
            catch (Exception artifactFailure)
            {
                artifactPath = $"<artifact write failed: {artifactFailure.Message}>";
            }

            throw new XunitException(
                $"ECS fuzz replay failed. Seed=0x{trace.Seed:x16}. " +
                $"Failure trace: {artifactPath}{Environment.NewLine}" +
                $"Original failure:{Environment.NewLine}{failure}{Environment.NewLine}" +
                $"Minimized replay trace:{Environment.NewLine}{minimized.ToJson()}");
        }
    }

    private static FuzzRunResult RunLongCampaignWithFailureArtifact(ulong seed, int steps)
    {
        try
        {
            // Full state verification is periodic and repeated at the final step. Acceptance,
            // rollback, structural metrics, and epoch checks still run for every generated step.
            return new EcsFuzzRunner().RunGenerated(
                seed,
                steps,
                EcsFuzzRunner.LongCampaignFullVerificationInterval);
        }
        catch (Exception failure)
        {
            string artifactPath;
            try
            {
                artifactPath = FuzzFailureArtifacts.WriteLongCampaignFailure(seed, steps, failure);
            }
            catch (Exception artifactFailure)
            {
                artifactPath = $"<artifact write failed: {artifactFailure.Message}>";
            }

            throw new XunitException(
                $"ECS long-fuzz campaign failed. Seed=0x{seed:x16}; requestedSteps={steps}. " +
                $"Raw deterministic failure artifact: {artifactPath}{Environment.NewLine}" +
                $"Failure:{Environment.NewLine}{failure}");
        }
    }

    private static FuzzStep Immediate(
        FuzzCommandKind kind,
        int entityId,
        int value = 0,
        int otherEntityId = 0) =>
        new(FuzzStepMode.Immediate, [Command(kind, entityId, value, otherEntityId)]);

    private static FuzzStep Batch(params FuzzCommand[] commands) =>
        new(FuzzStepMode.CommandBuffer, commands);

    private static FuzzCommand Command(
        FuzzCommandKind kind,
        int entityId,
        int value = 0,
        int otherEntityId = 0) =>
        new(kind, entityId, value, otherEntityId);
}
