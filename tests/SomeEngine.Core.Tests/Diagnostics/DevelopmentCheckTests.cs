using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Core.Tests.Diagnostics;

public sealed class DevelopmentCheckTests
{
    [Fact]
    public void RecoveredFailureReportEvaluatesItsMessageOnlyInDevelopmentCallers()
    {
        bool argumentEvaluated = false;

        DevelopmentCheck.Report(CreateMessage(ref argumentEvaluated));

#if DEBUG
        Assert.True(argumentEvaluated);
#else
        Assert.False(argumentEvaluated);
#endif
    }

    private static string CreateMessage(ref bool argumentEvaluated)
    {
        argumentEvaluated = true;
        return "recovered failure";
    }

    [Fact]
    public void RequireEvaluatesAndThrowsOnlyInDevelopmentCallers()
    {
        int evaluations = 0;

#if DEBUG
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentCheck.Require(EvaluateFalse(ref evaluations), "broken invariant"));
        Assert.Equal(1, evaluations);
#else
        DevelopmentCheck.Require(EvaluateFalse(ref evaluations), "broken invariant");
        Assert.Equal(0, evaluations);
#endif
    }

    private static bool EvaluateFalse(ref int evaluations)
    {
        evaluations++;
        return false;
    }
}
