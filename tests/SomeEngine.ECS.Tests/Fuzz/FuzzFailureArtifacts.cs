using System.Text.Json;

namespace SomeEngine.ECS.Fuzz.Tests;

internal static class FuzzDeltaDebugger
{
    internal static EcsFuzzTrace Minimize(
        EcsFuzzTrace trace,
        Func<EcsFuzzTrace, bool> stillFails,
        int maximumAttempts = 512)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(stillFails);
        if (maximumAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        var current = trace.Steps.ToList();
        int partitions = 2;
        int attempts = 0;
        while (current.Count >= 2 && attempts < maximumAttempts)
        {
            int chunkSize = (current.Count + partitions - 1) / partitions;
            bool reduced = false;
            for (int start = 0; start < current.Count && attempts < maximumAttempts; start += chunkSize)
            {
                int count = Math.Min(chunkSize, current.Count - start);
                var candidateSteps = new List<FuzzStep>(current.Count - count);
                candidateSteps.AddRange(current.Take(start));
                candidateSteps.AddRange(current.Skip(start + count));
                var candidate = trace with { Steps = candidateSteps.ToArray() };
                attempts++;
                if (!stillFails(candidate))
                    continue;

                current = candidateSteps;
                partitions = Math.Max(2, partitions - 1);
                reduced = true;
                break;
            }

            if (reduced)
                continue;
            if (partitions >= current.Count)
                break;
            partitions = Math.Min(current.Count, partitions * 2);
        }

        return trace with { Steps = current.ToArray() };
    }
}

internal sealed record FuzzFailureArtifact(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string ExceptionType,
    string FailureFingerprint,
    string ExceptionMessage,
    string? ExceptionStackTrace,
    EcsFuzzTrace OriginalTrace,
    EcsFuzzTrace MinimizedTrace);

internal sealed record LongFuzzFailureArtifact(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string PrngAlgorithm,
    string Seed,
    int RequestedSteps,
    int FailedStepIndex,
    string ExceptionType,
    string FailureFingerprint,
    string ExceptionMessage,
    string? ExceptionStackTrace);

internal static class FuzzFailureArtifacts
{
    internal static EcsFuzzTrace LoadReplayTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string json = File.ReadAllText(fullPath);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("minimizedTrace", out _))
        {
            FuzzFailureArtifact artifact = JsonSerializer.Deserialize<FuzzFailureArtifact>(
                    json,
                    EcsFuzzTrace.JsonOptions)
                ?? throw new InvalidDataException(
                    $"The ECS fuzz failure artifact '{fullPath}' contained no artifact.");
            if (artifact.SchemaVersion != 1)
            {
                throw new InvalidDataException(
                    $"Unsupported ECS fuzz failure artifact schema {artifact.SchemaVersion}.");
            }
            return artifact.MinimizedTrace
                ?? throw new InvalidDataException(
                    $"The ECS fuzz failure artifact '{fullPath}' contained no minimized trace.");
        }

        return EcsFuzzTrace.FromJson(json);
    }

    internal static string Write(
        EcsFuzzTrace original,
        EcsFuzzTrace minimized,
        Exception failure)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(minimized);
        ArgumentNullException.ThrowIfNull(failure);

        string directory = Path.Combine(AppContext.BaseDirectory, "fuzz-failures");
        Directory.CreateDirectory(directory);
        string fileName = $"ecs-fuzz-{original.Seed:x16}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json";
        string path = Path.Combine(directory, fileName);
        var artifact = new FuzzFailureArtifact(
            SchemaVersion: 1,
            CreatedUtc: DateTimeOffset.UtcNow,
            ExceptionType: failure.GetType().FullName ?? failure.GetType().Name,
            FailureFingerprint: Fingerprint(failure),
            ExceptionMessage: failure.Message,
            ExceptionStackTrace: failure.StackTrace,
            OriginalTrace: original,
            MinimizedTrace: minimized);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(artifact, EcsFuzzTrace.JsonOptions));
        return path;
    }

    internal static string WriteLongCampaignFailure(
        ulong seed,
        int requestedSteps,
        Exception failure)
    {
        if (requestedSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSteps));
        ArgumentNullException.ThrowIfNull(failure);

        string directory = Path.Combine(AppContext.BaseDirectory, "fuzz-failures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"ecs-long-fuzz-{seed:x16}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-" +
            $"{Guid.NewGuid():N}.json");
        string temporaryPath = path + $".tmp.{Guid.NewGuid():N}";
        int failedStepIndex = failure is FuzzFailureException fuzzFailure
            ? fuzzFailure.StepIndex
            : -1;
        var artifact = new LongFuzzFailureArtifact(
            SchemaVersion: 1,
            CreatedUtc: DateTimeOffset.UtcNow,
            PrngAlgorithm: EcsFuzzTrace.FixedPrngAlgorithm,
            Seed: $"0x{seed:x16}",
            RequestedSteps: requestedSteps,
            FailedStepIndex: failedStepIndex,
            ExceptionType: failure.GetType().FullName ?? failure.GetType().Name,
            FailureFingerprint: Fingerprint(failure),
            ExceptionMessage: failure.Message,
            ExceptionStackTrace: failure.StackTrace);
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(artifact, EcsFuzzTrace.JsonOptions));
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return path;
    }

    internal static string Fingerprint(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is FuzzFailureException fuzzFailure)
            return fuzzFailure.Fingerprint;
        return failure.GetType().FullName ?? failure.GetType().Name;
    }

    internal static Exception? ReplayFailure(EcsFuzzTrace trace)
    {
        try
        {
            _ = new EcsFuzzRunner().Run(trace);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }
}
