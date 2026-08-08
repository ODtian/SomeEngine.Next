using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SomeEngine.ECS.Fuzz.Tests;

internal sealed record LongFuzzCampaignEvidence(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string CommitSha,
    bool Clean,
    bool Passed,
    string PrngAlgorithm,
    string Seed,
    int Steps,
    int MaximumLogicalEntities,
    int FullVerificationInterval,
    double DurationMilliseconds,
    int SuccessfulBatches,
    int RejectedBatches,
    int RejectedImmediateOperations,
    string StateDigest);

internal static class LongFuzzEvidence
{
    internal const int SchemaVersion = 1;

    internal static void Write(
        string path,
        string commitSha,
        EcsFuzzTrace trace,
        FuzzRunResult result,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Write(
            path,
            commitSha,
            trace.PrngAlgorithm,
            trace.Seed,
            trace.Steps.Length,
            result,
            duration);
    }

    internal static void Write(
        string path,
        string commitSha,
        string prngAlgorithm,
        ulong seed,
        int stepCount,
        FuzzRunResult result,
        TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(prngAlgorithm);
        ArgumentNullException.ThrowIfNull(result);
        string normalizedCommit = commitSha.ToLowerInvariant();
        if (normalizedCommit.Length is not (40 or 64) ||
            !normalizedCommit.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                "Long-fuzz evidence requires the clean full Git commit captured by its launcher.");
        }
        if (result.StepCount != stepCount || result.StepCount <= 0)
            throw new InvalidDataException("Long-fuzz result step count does not match its campaign.");
        if (!string.Equals(prngAlgorithm, "xorshift64star-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Long-fuzz evidence requires the current deterministic PRNG.");
        if (duration <= TimeSpan.Zero || !double.IsFinite(duration.TotalMilliseconds))
            throw new InvalidDataException("Long-fuzz evidence requires a positive finite duration.");
        if (result.SuccessfulBatches < 0 ||
            result.RejectedBatches < 0 ||
            result.RejectedImmediateOperations < 0)
        {
            throw new InvalidDataException("Long-fuzz evidence counters cannot be negative.");
        }
        if (string.IsNullOrWhiteSpace(result.StateDigest))
            throw new InvalidDataException("Long-fuzz result state cannot be empty.");
        string stateDigest = HashState(result.StateDigest);

        var evidence = new LongFuzzCampaignEvidence(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            normalizedCommit,
            Clean: true,
            Passed: true,
            prngAlgorithm,
            $"0x{seed:x16}",
            result.StepCount,
            EcsFuzzTraceGenerator.MaximumLogicalEntities,
            EcsFuzzRunner.LongCampaignFullVerificationInterval,
            duration.TotalMilliseconds,
            result.SuccessfulBatches,
            result.RejectedBatches,
            result.RejectedImmediateOperations,
            stateDigest);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(evidence, EcsFuzzTrace.JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string HashState(string state)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Encoder encoder = Encoding.UTF8.GetEncoder();
        ReadOnlySpan<char> remaining = state.AsSpan();
        Span<byte> encoded = stackalloc byte[1024];
        while (!remaining.IsEmpty)
        {
            encoder.Convert(
                remaining,
                encoded,
                flush: true,
                out int charsUsed,
                out int bytesUsed,
                out _);
            if (charsUsed == 0 && bytesUsed == 0)
                throw new InvalidDataException("Long-fuzz state could not be encoded.");
            hash.AppendData(encoded[..bytesUsed]);
            remaining = remaining[charsUsed..];
        }
        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
            throw new CryptographicException("Long-fuzz state SHA-256 could not be finalized.");
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
