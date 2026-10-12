using SomeEngine.Runtime;

namespace SomeEngine.Runtime.Tests;

public sealed class ShadowResolutionReportTests
{
    [Fact]
    public void MissingFeedbackDoesNotClaimZeroShadowBias()
    {
        ShadowResolutionSummary empty = ShadowResolutionSummary.FromFeedback(true, []);
        Assert.True(empty.Enabled);
        Assert.Equal(0, empty.CompletedFeedbacks);
        Assert.Null(empty.FirstFeedbackFrame);
        Assert.Null(empty.LastFeedbackFrame);
        Assert.Null(empty.MinimumLodBias);
        Assert.Null(empty.MaximumLodBias);
        Assert.Null(empty.MeanLodBias);
        Assert.Null(empty.LastLodBias);

        ShadowResolutionSummary zero = ShadowResolutionSummary.FromFeedback(true, [(11, 0f)]);
        Assert.Equal(1, zero.CompletedFeedbacks);
        Assert.Equal(0f, zero.MinimumLodBias);
        Assert.Equal(0f, zero.MaximumLodBias);
        Assert.Equal(0d, zero.MeanLodBias);
        Assert.Equal(0f, zero.LastLodBias);
    }

    [Fact]
    public void SummaryCountsCompletedFeedbackAndPreservesItsBiasRange()
    {
        ShadowResolutionSummary summary = ShadowResolutionSummary.FromFeedback(true,
            [(11, 0f), (13, 2f), (14, 1f)]);
        Assert.Equal(3, summary.CompletedFeedbacks);
        Assert.Equal(11ul, summary.FirstFeedbackFrame);
        Assert.Equal(14ul, summary.LastFeedbackFrame);
        Assert.Equal(0f, summary.MinimumLodBias);
        Assert.Equal(2f, summary.MaximumLodBias);
        Assert.Equal(1d, summary.MeanLodBias);
        Assert.Equal(1f, summary.LastLodBias);
    }
}
