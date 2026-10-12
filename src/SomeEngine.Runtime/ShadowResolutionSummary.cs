namespace SomeEngine.Runtime;

/// <summary>Actual completed shadow feedback observed during a scene measurement window.</summary>
public sealed record ShadowResolutionSummary(
    bool Enabled, int CompletedFeedbacks, ulong? FirstFeedbackFrame, ulong? LastFeedbackFrame,
    float? MinimumLodBias, float? MaximumLodBias, double? MeanLodBias, float? LastLodBias)
{
    public static ShadowResolutionSummary FromFeedback(
        bool enabled, ReadOnlySpan<(ulong Frame, float LodBias)> feedback)
    {
        if (feedback.IsEmpty) return new(enabled, 0, null, null, null, null, null, null);
        float minimum = feedback[0].LodBias;
        float maximum = minimum;
        double sum = 0;
        foreach (var sample in feedback)
        {
            minimum = Math.Min(minimum, sample.LodBias);
            maximum = Math.Max(maximum, sample.LodBias);
            sum += sample.LodBias;
        }
        return new(enabled, feedback.Length, feedback[0].Frame, feedback[^1].Frame,
            minimum, maximum, sum / feedback.Length, feedback[^1].LodBias);
    }
}
