namespace SomeEngine.Render.Cluster;

/// <summary>Identifies completed feedback by its residency epoch and delivery sequence.</summary>
internal readonly record struct ClusterReadbackTicket(
    ClusterEpochId EpochId,
    ulong FeedbackSequence)
{
    internal bool IsValid => EpochId.IsValid && FeedbackSequence != 0;
}
