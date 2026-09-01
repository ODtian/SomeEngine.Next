namespace SomeEngine.Render.Cluster;

/// <summary>
/// Authenticates delayed GPU feedback with both its resource epoch and the submitted frame that
/// produced it. A ticket is created only after successful submission and cannot describe the
/// frame that is still being recorded.
/// </summary>
internal readonly record struct ClusterReadbackTicket(
    ClusterEpochId EpochId,
    ulong SubmittedFrame)
{
    internal bool IsValid => EpochId.IsValid && SubmittedFrame != 0;
}
