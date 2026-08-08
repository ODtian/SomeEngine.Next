using System.Security.Cryptography;
using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class CertificationEvidenceManifestTests
{
    private const string CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CompleteManifest_BindsEveryRequiredExternalArtifact()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(directory, files, includePowerCut: true);

        CertificationEvidenceBinding binding = CertificationEvidenceManifest.ValidateCore(
            manifestPath,
            files.BaselinePath,
            files.BudgetsPath,
            new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true));

        Assert.True(binding.AllRequiredEvidencePresent);
        Assert.Equal(CommitSha, binding.CommitSha);
        Assert.Equal(["win-x64"], binding.ClaimedRids);
        Assert.Equal(["NTFS-on-target-nvme"], binding.PowerCutTargets);
        Assert.Equal(CertificationEvidenceManifest.MinimumLongFuzzSteps, binding.LongFuzzMinimumSteps);
        Assert.All(
            new[]
            {
                binding.ManifestSha256,
                binding.ApprovedBaselineSha256,
                binding.AbsoluteBudgetsSha256,
                binding.MachineManifestSha256,
                Assert.Single(binding.AotEvidenceSha256),
                Assert.Single(binding.LongFuzzEvidenceSha256),
                Assert.Single(binding.PowerCutEvidenceSha256),
            },
            static hash => Assert.Matches("^[0-9a-f]{64}$", hash));
    }

    [Fact]
    public void MissingPowerCutEvidence_CannotProduceCertificationBinding()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(directory, files, includePowerCut: false);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CertificationEvidenceManifest.ValidateCore(
                manifestPath,
                files.BaselinePath,
                files.BudgetsPath,
                new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true)));

        Assert.Contains("power-cut evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArtifactHashMismatch_CannotProduceCertificationBinding()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(
            directory,
            files with { MachineSha256 = new string('0', 64) },
            includePowerCut: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CertificationEvidenceManifest.ValidateCore(
                manifestPath,
                files.BaselinePath,
                files.BudgetsPath,
                new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true)));

        Assert.Contains("machine artifact sha256", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteLongFuzzEvidence_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        File.WriteAllText(
            files.FuzzPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                commitSha = CommitSha,
                clean = true,
                passed = true,
                prngAlgorithm = "xorshift64star-v1",
                steps = CertificationEvidenceManifest.MinimumLongFuzzSteps,
            }, EcsBenchmarkReport.JsonOptions));
        files = files with { FuzzSha256 = Hash(files.FuzzPath) };
        string manifestPath = WriteManifest(directory, files, includePowerCut: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CertificationEvidenceManifest.ValidateCore(
                manifestPath,
                files.BaselinePath,
                files.BudgetsPath,
                new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true)));

        Assert.Contains("exact required shape", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateLongFuzzArtifact_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(
            directory,
            files,
            includePowerCut: true,
            duplicateLongFuzz: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CertificationEvidenceManifest.ValidateCore(
                manifestPath,
                files.BaselinePath,
                files.BudgetsPath,
                new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true)));

        Assert.Contains("repeated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundInputChangedAfterValidation_FailsFinalRevalidation()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(directory, files, includePowerCut: true);
        CertificationEvidenceBinding binding = CertificationEvidenceManifest.ValidateCore(
            manifestPath,
            files.BaselinePath,
            files.BudgetsPath,
            new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true));

        File.AppendAllText(files.MachinePath, "changed");

        BenchmarkConfigurationException exception = Assert.Throws<BenchmarkConfigurationException>(
            () => binding.ValidationState!.VerifyUnchanged());
        Assert.Contains("changed while", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputCannotCollideWithAnyReferencedArtifact()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(directory, files, includePowerCut: true);
        CertificationEvidenceBinding binding = CertificationEvidenceManifest.ValidateCore(
            manifestPath,
            files.BaselinePath,
            files.BudgetsPath,
            new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => binding.ValidationState!.RejectOutputCollision(files.FuzzPath));

        Assert.Contains("must not overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerCutTargetsAndHashes_AreNormalizedAsPairs()
    {
        using var directory = new TemporaryDirectory();
        EvidenceFiles files = WriteEvidenceFiles(directory);
        string manifestPath = WriteManifest(
            directory,
            files,
            includePowerCut: true,
            includeSecondPowerCut: true);

        CertificationEvidenceBinding binding = CertificationEvidenceManifest.ValidateCore(
            manifestPath,
            files.BaselinePath,
            files.BudgetsPath,
            new EcsBenchmarkSourceRevision(CommitSha, GitWorkingTreeClean: true));

        Assert.Equal(["A-filesystem", "NTFS-on-target-nvme"], binding.PowerCutTargets);
        Assert.Equal([files.SecondPowerSha256, files.PowerSha256], binding.PowerCutEvidenceSha256);
    }

    private static EvidenceFiles WriteEvidenceFiles(TemporaryDirectory directory)
    {
        string scenario = "manifest-validation";
        string baselinePath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [scenario]);
        string budgetsPath = BenchmarkTestData.WriteBudget(directory);
        string machinePath = directory.Write(
            "machine.json",
            "{\"machineId\":\"cert-host\",\"cpu\":\"reviewed externally\"}");
        string aotPath = directory.Write(
            "aot.json",
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                createdUtc = DateTimeOffset.UtcNow.ToString("O"),
                commitSha = CommitSha,
                clean = true,
                sdkVersion = "10.0.100",
                machineName = "cert-host",
                hostFramework = ".NET test",
                hostOperatingSystem = "test-os",
                results = new[]
                {
                    new
                    {
                        rid = "win-x64",
                        executed = true,
                        exitCode = 0,
                        executableSha256 = new string('1', 64),
                    },
                },
            }, EcsBenchmarkReport.JsonOptions));
        string fuzzPath = directory.Write(
            "fuzz.json",
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                createdUtc = DateTimeOffset.UtcNow.ToString("O"),
                commitSha = CommitSha,
                clean = true,
                passed = true,
                prngAlgorithm = "xorshift64star-v1",
                seed = "0x0000000000000001",
                steps = CertificationEvidenceManifest.MinimumLongFuzzSteps,
                maximumLogicalEntities = 1_024,
                fullVerificationInterval = 128,
                durationMilliseconds = 1.0,
                successfulBatches = 1,
                rejectedBatches = 0,
                rejectedImmediateOperations = 0,
                stateDigest = new string('a', 64),
            }, EcsBenchmarkReport.JsonOptions));
        string powerPath = directory.Write(
            "power-cut.txt",
            "Operator-reviewed process-kill and physical power-cut log.");
        string secondPowerPath = directory.Write(
            "power-cut-second.txt",
            "Second independently reviewed power-cut log.");
        return new EvidenceFiles(
            baselinePath,
            budgetsPath,
            machinePath,
            Hash(machinePath),
            aotPath,
            Hash(aotPath),
            fuzzPath,
            Hash(fuzzPath),
            powerPath,
            Hash(powerPath),
            secondPowerPath,
            Hash(secondPowerPath));
    }

    private static string WriteManifest(
        TemporaryDirectory directory,
        EvidenceFiles files,
        bool includePowerCut,
        bool duplicateLongFuzz = false,
        bool includeSecondPowerCut = false)
    {
        var powerCutEvidence = new List<object>();
        if (includePowerCut)
        {
            powerCutEvidence.Add(new
            {
                targetFilesystem = "NTFS-on-target-nvme",
                artifact = Artifact(files.PowerPath, files.PowerSha256),
                processKillPassed = true,
                powerCutPassed = true,
                primarySlotRecoveryPassed = true,
                previousSlotRecoveryPassed = true,
            });
        }
        if (includeSecondPowerCut)
        {
            powerCutEvidence.Add(new
            {
                targetFilesystem = "A-filesystem",
                artifact = Artifact(files.SecondPowerPath, files.SecondPowerSha256),
                processKillPassed = true,
                powerCutPassed = true,
                primarySlotRecoveryPassed = true,
                previousSlotRecoveryPassed = true,
            });
        }
        object fuzzArtifact = Artifact(files.FuzzPath, files.FuzzSha256);
        object[] fuzzArtifacts = duplicateLongFuzz
            ? [fuzzArtifact, fuzzArtifact]
            : [fuzzArtifact];
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = CertificationEvidenceManifest.SchemaVersion,
            commitSha = CommitSha,
            benchmarkReportSchemaVersion = EcsBenchmarkSuite.ReportSchemaVersion,
            approvedBaselineSha256 = Hash(files.BaselinePath),
            absoluteBudgetsSha256 = Hash(files.BudgetsPath),
            machine = new
            {
                machineId = "cert-host",
                artifact = Artifact(files.MachinePath, files.MachineSha256),
            },
            claimedRids = new[] { "win-x64" },
            aotEvidence = new[] { Artifact(files.AotPath, files.AotSha256) },
            longFuzzEvidence = fuzzArtifacts,
            powerCutEvidence,
        }, EcsBenchmarkReport.JsonOptions);
        return directory.Write("evidence-manifest.json", json);
    }

    private static object Artifact(string path, string sha256) => new
    {
        path = Path.GetFileName(path),
        sha256,
    };

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record EvidenceFiles(
        string BaselinePath,
        string BudgetsPath,
        string MachinePath,
        string MachineSha256,
        string AotPath,
        string AotSha256,
        string FuzzPath,
        string FuzzSha256,
        string PowerPath,
        string PowerSha256,
        string SecondPowerPath,
        string SecondPowerSha256);
}
