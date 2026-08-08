using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.ECS.Benchmarks;

internal static class CertificationEvidenceManifest
{
    internal const int SchemaVersion = 1;
    internal const int MinimumLongFuzzSteps = 10_000;

    private static readonly HashSet<string> AotRootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "createdUtc",
        "commitSha",
        "clean",
        "sdkVersion",
        "machineName",
        "hostFramework",
        "hostOperatingSystem",
        "results",
    };

    private static readonly HashSet<string> AotResultProperties = new(StringComparer.Ordinal)
    {
        "rid",
        "executed",
        "exitCode",
        "executableSha256",
    };

    private static readonly HashSet<string> LongFuzzProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "createdUtc",
        "commitSha",
        "clean",
        "passed",
        "prngAlgorithm",
        "seed",
        "steps",
        "maximumLogicalEntities",
        "fullVerificationInterval",
        "durationMilliseconds",
        "successfulBatches",
        "rejectedBatches",
        "rejectedImmediateOperations",
        "stateDigest",
    };

    internal static CertificationEvidenceBinding? Validate(
        BenchmarkOptions options,
        EcsBenchmarkSourceRevision sourceRevision)
    {
        if (options.Profile != BenchmarkProfile.Certification)
            return null;

        if (options.EvidenceManifestPath is null ||
            options.BaselinePath is null ||
            options.AbsoluteBudgetsPath is null)
        {
            throw new BenchmarkConfigurationException(
                "Certification requires an evidence manifest, approved baseline, and absolute budgets.");
        }

        try
        {
            CertificationEvidenceBinding binding = ValidateCore(
                options.EvidenceManifestPath,
                options.BaselinePath,
                options.AbsoluteBudgetsPath,
                sourceRevision);
            binding.ValidationState!.RejectOutputCollision(options.OutputPath);
            return binding;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            InvalidOperationException or
            NotSupportedException or
            ArgumentException)
        {
            throw new BenchmarkConfigurationException(
                $"Certification evidence manifest validation failed: {exception.Message}");
        }
    }

    internal static CertificationEvidenceBinding ValidateCore(
        string manifestPath,
        string baselinePath,
        string absoluteBudgetsPath,
        EcsBenchmarkSourceRevision sourceRevision)
    {
        var validationState = new CertificationEvidenceValidationState();
        CertificationFileSnapshot manifestSnapshot = validationState.ReadUnique(
            manifestPath,
            "evidence manifest");
        EnsureNoDuplicateProperties(manifestSnapshot.Bytes, "evidence manifest");
        CertificationEvidenceManifestDocument manifest =
            JsonSerializer.Deserialize<CertificationEvidenceManifestDocument>(
                manifestSnapshot.Bytes,
                EcsBenchmarkReport.JsonOptions)
            ?? throw new InvalidDataException("The evidence manifest contained no document.");

        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"The evidence manifest must use schemaVersion {SchemaVersion}.");
        }
        if (!sourceRevision.IsCleanCommit ||
            !IsFullSha(manifest.CommitSha) ||
            !string.Equals(
                manifest.CommitSha,
                sourceRevision.GitCommitSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The evidence manifest commitSha must match the current clean full Git commit.");
        }
        if (manifest.BenchmarkReportSchemaVersion != EcsBenchmarkSuite.ReportSchemaVersion)
        {
            throw new InvalidDataException(
                $"The evidence manifest must bind benchmark report schema " +
                $"{EcsBenchmarkSuite.ReportSchemaVersion}.");
        }

        CertificationFileSnapshot baselineSnapshot = validationState.ReadUnique(
            baselinePath,
            "approved baseline");
        CertificationFileSnapshot budgetsSnapshot = validationState.ReadUnique(
            absoluteBudgetsPath,
            "absolute budgets");
        RequireHashMatch(
            manifest.ApprovedBaselineSha256,
            baselineSnapshot.Sha256,
            "approvedBaselineSha256");
        RequireHashMatch(
            manifest.AbsoluteBudgetsSha256,
            budgetsSnapshot.Sha256,
            "absoluteBudgetsSha256");
        validationState.BindGateInputs(
            BenchmarkGateEvaluator.BaselineCatalog.Load(
                baselineSnapshot.Bytes,
                baselineSnapshot.FullPath),
            BenchmarkGateEvaluator.AbsoluteBudgetCatalog.Load(
                budgetsSnapshot.Bytes,
                budgetsSnapshot.FullPath));

        string manifestDirectory = Path.GetDirectoryName(manifestSnapshot.FullPath)
            ?? throw new InvalidDataException("The evidence manifest must have a parent directory.");
        if (manifest.Machine is null || string.IsNullOrWhiteSpace(manifest.Machine.MachineId))
            throw new InvalidDataException("The evidence manifest must identify a target machine.");
        CertificationFileSnapshot machineSnapshot = ResolveAndVerifyArtifact(
            validationState,
            manifestDirectory,
            manifest.Machine.Artifact,
            "machine artifact");

        if (manifest.ClaimedRids is null || manifest.ClaimedRids.Length == 0)
            throw new InvalidDataException("The evidence manifest must declare at least one claimed RID.");
        var claimedRids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rid in manifest.ClaimedRids)
        {
            if (string.IsNullOrWhiteSpace(rid) || !claimedRids.Add(rid))
                throw new InvalidDataException("The evidence manifest claimedRids must be non-empty and unique.");
        }

        if (manifest.AotEvidence is null || manifest.AotEvidence.Length == 0)
            throw new InvalidDataException("The evidence manifest must attach NativeAOT execution evidence.");
        var executedRids = new HashSet<string>(StringComparer.Ordinal);
        string[] aotHashes = manifest.AotEvidence
            .Select(artifact => ValidateAotEvidence(
                validationState,
                manifestDirectory,
                artifact,
                sourceRevision.GitCommitSha,
                executedRids))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!claimedRids.SetEquals(executedRids))
        {
            throw new InvalidDataException(
                "Executed NativeAOT evidence RIDs must exactly equal claimedRids.");
        }

        if (manifest.LongFuzzEvidence is null || manifest.LongFuzzEvidence.Length == 0)
            throw new InvalidDataException("The evidence manifest must attach long-fuzz evidence.");
        var fuzzHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (CertificationArtifact artifact in manifest.LongFuzzEvidence)
        {
            string hash = ValidateLongFuzzEvidence(
                validationState,
                manifestDirectory,
                artifact,
                sourceRevision.GitCommitSha);
            if (!fuzzHashes.Add(hash))
            {
                throw new InvalidDataException(
                    "The evidence manifest repeats a long-fuzz artifact or artifact digest.");
            }
        }

        if (manifest.PowerCutEvidence is null || manifest.PowerCutEvidence.Length == 0)
            throw new InvalidDataException("The evidence manifest must attach power-cut evidence.");
        var powerCutTargets = new HashSet<string>(StringComparer.Ordinal);
        var powerCutBindings = new List<CertificationPowerCutBinding>(
            manifest.PowerCutEvidence.Length);
        foreach (CertificationPowerCutEvidence evidence in manifest.PowerCutEvidence)
        {
            if (string.IsNullOrWhiteSpace(evidence.TargetFilesystem) ||
                !powerCutTargets.Add(evidence.TargetFilesystem))
            {
                throw new InvalidDataException(
                    "Power-cut evidence targetFilesystem values must be non-empty and unique.");
            }
            if (!evidence.ProcessKillPassed ||
                !evidence.PowerCutPassed ||
                !evidence.PrimarySlotRecoveryPassed ||
                !evidence.PreviousSlotRecoveryPassed)
            {
                throw new InvalidDataException(
                    $"Power-cut evidence for '{evidence.TargetFilesystem}' is incomplete or failed.");
            }
            CertificationFileSnapshot snapshot = ResolveAndVerifyArtifact(
                validationState,
                manifestDirectory,
                evidence.Artifact,
                $"power-cut artifact for {evidence.TargetFilesystem}");
            powerCutBindings.Add(new CertificationPowerCutBinding(
                evidence.TargetFilesystem,
                snapshot.Sha256));
        }
        powerCutBindings.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.TargetFilesystem, right.TargetFilesystem));

        return new CertificationEvidenceBinding(
            ManifestSha256: manifestSnapshot.Sha256,
            CommitSha: sourceRevision.GitCommitSha,
            ApprovedBaselineSha256: baselineSnapshot.Sha256,
            AbsoluteBudgetsSha256: budgetsSnapshot.Sha256,
            MachineId: manifest.Machine.MachineId,
            MachineManifestSha256: machineSnapshot.Sha256,
            ClaimedRids: claimedRids.Order(StringComparer.Ordinal).ToArray(),
            AotEvidenceSha256: aotHashes,
            LongFuzzEvidenceSha256: fuzzHashes.Order(StringComparer.Ordinal).ToArray(),
            LongFuzzMinimumSteps: MinimumLongFuzzSteps,
            PowerCutTargets: powerCutBindings.Select(static item => item.TargetFilesystem).ToArray(),
            PowerCutEvidenceSha256: powerCutBindings.Select(static item => item.ArtifactSha256).ToArray(),
            AllRequiredEvidencePresent: true)
        {
            ValidationState = validationState,
        };
    }

    private static string ValidateAotEvidence(
        CertificationEvidenceValidationState validationState,
        string manifestDirectory,
        CertificationArtifact artifact,
        string expectedCommitSha,
        HashSet<string> executedRids)
    {
        CertificationFileSnapshot snapshot = ResolveAndVerifyArtifact(
            validationState,
            manifestDirectory,
            artifact,
            "NativeAOT artifact");
        using JsonDocument document = JsonDocument.Parse(snapshot.Bytes);
        JsonElement root = document.RootElement;
        EnsureExactProperties(root, AotRootProperties, $"NativeAOT evidence '{snapshot.FullPath}'");
        if (ReadInt32(root, "schemaVersion") != 2 ||
            !ReadBoolean(root, "clean") ||
            !string.Equals(ReadString(root, "commitSha"), expectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !TryReadTimestamp(root, "createdUtc") ||
            string.IsNullOrWhiteSpace(ReadString(root, "sdkVersion")) ||
            string.IsNullOrWhiteSpace(ReadString(root, "machineName")) ||
            string.IsNullOrWhiteSpace(ReadString(root, "hostFramework")) ||
            string.IsNullOrWhiteSpace(ReadString(root, "hostOperatingSystem")))
        {
            throw new InvalidDataException(
                $"NativeAOT evidence '{snapshot.FullPath}' does not bind the current clean commit.");
        }
        JsonElement results = ReadArray(root, "results");
        if (results.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                $"NativeAOT evidence '{snapshot.FullPath}' has no executed RID results.");
        }
        foreach (JsonElement result in results.EnumerateArray())
        {
            EnsureExactProperties(
                result,
                AotResultProperties,
                $"NativeAOT evidence result in '{snapshot.FullPath}'");
            string rid = ReadString(result, "rid");
            if (!ReadBoolean(result, "executed") ||
                ReadInt32(result, "exitCode") != 0 ||
                !IsSha256(ReadString(result, "executableSha256")) ||
                !executedRids.Add(rid))
            {
                throw new InvalidDataException(
                    $"NativeAOT evidence '{snapshot.FullPath}' contains a failed, incomplete, " +
                    "or duplicate RID result.");
            }
        }
        return snapshot.Sha256;
    }

    private static string ValidateLongFuzzEvidence(
        CertificationEvidenceValidationState validationState,
        string manifestDirectory,
        CertificationArtifact artifact,
        string expectedCommitSha)
    {
        CertificationFileSnapshot snapshot = ResolveAndVerifyArtifact(
            validationState,
            manifestDirectory,
            artifact,
            "long-fuzz artifact");
        using JsonDocument document = JsonDocument.Parse(snapshot.Bytes);
        JsonElement root = document.RootElement;
        EnsureExactProperties(root, LongFuzzProperties, $"Long-fuzz evidence '{snapshot.FullPath}'");
        string stateDigest = ReadString(root, "stateDigest");
        if (ReadInt32(root, "schemaVersion") != 1 ||
            !ReadBoolean(root, "clean") ||
            !ReadBoolean(root, "passed") ||
            !string.Equals(ReadString(root, "commitSha"), expectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
            !TryReadTimestamp(root, "createdUtc") ||
            !IsNormalizedSeed(ReadString(root, "seed")) ||
            ReadInt32(root, "steps") < MinimumLongFuzzSteps ||
            ReadInt32(root, "maximumLogicalEntities") != 1_024 ||
            ReadInt32(root, "fullVerificationInterval") != 128 ||
            !string.Equals(
                ReadString(root, "prngAlgorithm"),
                "xorshift64star-v1",
                StringComparison.Ordinal) ||
            ReadPositiveFiniteDouble(root, "durationMilliseconds") <= 0 ||
            ReadInt32(root, "successfulBatches") < 0 ||
            ReadInt32(root, "rejectedBatches") < 0 ||
            ReadInt32(root, "rejectedImmediateOperations") < 0 ||
            !IsLowerSha256(stateDigest))
        {
            throw new InvalidDataException(
                $"Long-fuzz evidence '{snapshot.FullPath}' is incomplete, failed, too short, " +
                "or bound to another source revision.");
        }
        return snapshot.Sha256;
    }

    private static CertificationFileSnapshot ResolveAndVerifyArtifact(
        CertificationEvidenceValidationState validationState,
        string manifestDirectory,
        CertificationArtifact? artifact,
        string description)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Path))
            throw new InvalidDataException($"The evidence manifest is missing its {description} path.");
        string path = Path.GetFullPath(Path.Combine(manifestDirectory, artifact.Path));
        CertificationFileSnapshot snapshot = validationState.ReadUnique(path, description);
        RequireHashMatch(artifact.Sha256, snapshot.Sha256, $"{description} sha256");
        validationState.RegisterUniqueArtifactHash(snapshot.Sha256, description);
        return snapshot;
    }

    private static void EnsureNoDuplicateProperties(ReadOnlyMemory<byte> json, string description)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Visit(document.RootElement);
        return;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"The {description} repeats JSON property '{property.Name}'.");
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private static void EnsureExactProperties(
        JsonElement element,
        HashSet<string> expected,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{description} must contain an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"{description} has unknown or duplicate property '{property.Name}'.");
            }
        }
        if (!seen.SetEquals(expected))
            throw new InvalidDataException($"{description} does not contain its exact required shape.");
    }

    private static void RequireHashMatch(string? declared, string actual, string property)
    {
        if (!IsSha256(declared) || !string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Evidence property '{property}' does not match the artifact SHA-256.");
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(IsLowerHex);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(IsLowerHex);

    private static bool IsNormalizedSeed(string value)
    {
        if (value.Length != 18 || !value.StartsWith("0x", StringComparison.Ordinal))
            return false;
        for (int index = 2; index < value.Length; index++)
        {
            if (!IsLowerHex(value[index]))
                return false;
        }
        return true;
    }

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static JsonElement ReadProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
            throw new InvalidDataException($"Evidence JSON is missing property '{name}'.");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
    {
        JsonElement value = ReadProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Evidence property '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static int ReadInt32(JsonElement parent, string name)
    {
        JsonElement value = ReadProperty(parent, name);
        if (!value.TryGetInt32(out int result))
            throw new InvalidDataException($"Evidence property '{name}' must be an Int32.");
        return result;
    }

    private static double ReadPositiveFiniteDouble(JsonElement parent, string name)
    {
        JsonElement value = ReadProperty(parent, name);
        if (!value.TryGetDouble(out double result) || !double.IsFinite(result) || result <= 0)
        {
            throw new InvalidDataException(
                $"Evidence property '{name}' must be positive and finite.");
        }
        return result;
    }

    private static bool ReadBoolean(JsonElement parent, string name)
    {
        JsonElement value = ReadProperty(parent, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Evidence property '{name}' must be Boolean.");
        return value.GetBoolean();
    }

    private static JsonElement ReadArray(JsonElement parent, string name)
    {
        JsonElement value = ReadProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Evidence property '{name}' must be an array.");
        return value;
    }

    private static bool TryReadTimestamp(JsonElement parent, string name) =>
        DateTimeOffset.TryParseExact(
            ReadString(parent, name),
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);
}

internal sealed record CertificationEvidenceBinding(
    string ManifestSha256,
    string CommitSha,
    string ApprovedBaselineSha256,
    string AbsoluteBudgetsSha256,
    string MachineId,
    string MachineManifestSha256,
    string[] ClaimedRids,
    string[] AotEvidenceSha256,
    string[] LongFuzzEvidenceSha256,
    int LongFuzzMinimumSteps,
    string[] PowerCutTargets,
    string[] PowerCutEvidenceSha256,
    bool AllRequiredEvidencePresent)
{
    [JsonIgnore]
    internal CertificationEvidenceValidationState? ValidationState { get; init; }
}

internal sealed class CertificationEvidenceValidationState
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Dictionary<string, string> _fileHashes = new(PathComparer);
    private readonly HashSet<string> _artifactHashes = new(StringComparer.Ordinal);

    internal BenchmarkGateEvaluator.BaselineCatalog Baseline { get; private set; } = null!;
    internal BenchmarkGateEvaluator.AbsoluteBudgetCatalog Budgets { get; private set; } = null!;

    internal CertificationFileSnapshot ReadUnique(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        if (_fileHashes.ContainsKey(fullPath))
            throw new InvalidDataException($"Certification input path '{fullPath}' is repeated.");
        byte[] bytes = File.ReadAllBytes(fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _fileHashes.Add(fullPath, sha256);
        return new CertificationFileSnapshot(fullPath, bytes, sha256, description);
    }

    internal void RegisterUniqueArtifactHash(string sha256, string description)
    {
        if (!_artifactHashes.Add(sha256))
        {
            throw new InvalidDataException(
                $"Certification evidence repeats the content of {description}.");
        }
    }

    internal void BindGateInputs(
        BenchmarkGateEvaluator.BaselineCatalog baseline,
        BenchmarkGateEvaluator.AbsoluteBudgetCatalog budgets)
    {
        Baseline = baseline;
        Budgets = budgets;
    }

    internal void RejectOutputCollision(string? outputPath)
    {
        if (outputPath is null)
            return;
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (_fileHashes.ContainsKey(fullOutputPath))
        {
            throw new InvalidDataException(
                "Certification --output must not overwrite any validated input or evidence artifact.");
        }
    }

    internal void VerifyUnchanged()
    {
        foreach ((string path, string expectedHash) in _fileHashes)
        {
            string actualHash;
            try
            {
                using FileStream stream = File.OpenRead(path);
                actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BenchmarkConfigurationException(
                    $"Certification input '{path}' could not be revalidated: {exception.Message}");
            }
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new BenchmarkConfigurationException(
                    $"Certification input '{path}' changed while benchmark evidence was collected.");
            }
        }
    }
}

internal sealed record CertificationFileSnapshot(
    string FullPath,
    byte[] Bytes,
    string Sha256,
    string Description);

internal sealed record CertificationPowerCutBinding(
    string TargetFilesystem,
    string ArtifactSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CertificationEvidenceManifestDocument(
    int SchemaVersion,
    string CommitSha,
    int BenchmarkReportSchemaVersion,
    string ApprovedBaselineSha256,
    string AbsoluteBudgetsSha256,
    CertificationMachineEvidence Machine,
    string[] ClaimedRids,
    CertificationArtifact[] AotEvidence,
    CertificationArtifact[] LongFuzzEvidence,
    CertificationPowerCutEvidence[] PowerCutEvidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CertificationMachineEvidence(
    string MachineId,
    CertificationArtifact Artifact);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CertificationArtifact(string Path, string Sha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CertificationPowerCutEvidence(
    string TargetFilesystem,
    CertificationArtifact Artifact,
    bool ProcessKillPassed,
    bool PowerCutPassed,
    bool PrimarySlotRecoveryPassed,
    bool PreviousSlotRecoveryPassed);
