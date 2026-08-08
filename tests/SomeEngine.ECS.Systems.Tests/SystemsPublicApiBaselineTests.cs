using SomeEngine.Testing;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SystemsPublicApiBaselineTests
{
    private const string ExpectedSha256 = "0977DF9B411EF32151FEB4B23DA5191A583E863220D15A8797C5DD791AE96A14";

    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string surface = PublicApiSurface.Build(typeof(ISystemDriver<>).Assembly);
        string actual = PublicApiSurface.Sha256(surface);

        Assert.True(
            string.Equals(ExpectedSha256, actual, StringComparison.Ordinal),
            PublicApiSurface.FailureMessage("SomeEngine.ECS.Systems", ExpectedSha256, actual, surface));
    }
}
