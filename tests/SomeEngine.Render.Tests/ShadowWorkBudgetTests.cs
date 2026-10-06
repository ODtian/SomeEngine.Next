using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Tests;

public sealed class ShadowWorkBudgetTests
{
    [Fact]
    public void TimeBudgetEnablesWorkFeedbackWithTheDefaultLoadBudget()
    {
        var defaults = new VirtualShadowMapSettings();
        Assert.Equal(0f, defaults.WorkLoadBudget);
        Assert.Equal(0f, defaults.WorkTimeBudgetMilliseconds);
        Assert.True((defaults with { WorkTimeBudgetMilliseconds = 1f }).WorkThrottlingEnabled);
    }
}
