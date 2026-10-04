namespace SomeEngine.Views.Tests;

public sealed class JobBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JobResultsAppearOnTheRebuildThread(bool completed) =>
        JobBuildChecks.VerifyResult(completed);
}
