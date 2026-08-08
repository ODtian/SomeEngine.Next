namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class SourceRevisionTests
{
    private static readonly EcsBenchmarkSourceRevision CleanRevision =
        new(new string('a', 40), GitWorkingTreeClean: true);

    [Fact]
    public void CertificationAcceptsOneStableCleanRevision()
    {
        EcsBenchmarkSuite.ValidateCertificationSourceRevision(
            BenchmarkProfile.Certification,
            CleanRevision,
            CleanRevision);
    }

    [Fact]
    public void CertificationRejectsDirtyInitialRevisionBeforeWorkStarts()
    {
        EcsBenchmarkSourceRevision dirty = CleanRevision with { GitWorkingTreeClean = false };

        BenchmarkConfigurationException exception =
            Assert.Throws<BenchmarkConfigurationException>(() =>
                EcsBenchmarkSuite.ValidateCertificationSourceRevision(
                    BenchmarkProfile.Certification,
                    dirty,
                    dirty));

        Assert.Contains("before collecting evidence", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CertificationRejectsSourceChangesBeforeReportEmission(bool changeHead)
    {
        EcsBenchmarkSourceRevision completed = changeHead
            ? CleanRevision with { GitCommitSha = new string('b', 40) }
            : CleanRevision with { GitWorkingTreeClean = false };

        BenchmarkConfigurationException exception =
            Assert.Throws<BenchmarkConfigurationException>(() =>
                EcsBenchmarkSuite.ValidateCertificationSourceRevision(
                    BenchmarkProfile.Certification,
                    CleanRevision,
                    completed));

        Assert.Contains("changed while certification was running", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCertificationProfilesDoNotRequireRepositoryEvidence()
    {
        var unavailable = new EcsBenchmarkSourceRevision(string.Empty, GitWorkingTreeClean: false);

        EcsBenchmarkSuite.ValidateCertificationSourceRevision(
            BenchmarkProfile.Smoke,
            unavailable,
            unavailable);
    }
}
