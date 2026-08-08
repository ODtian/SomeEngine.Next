namespace SomeEngine.Render.Frame;

/// <summary>Immutable diagnostics for the render-frame read/write synchronization boundary.</summary>
public readonly record struct RenderFrameSynchronizationDiagnostics(
    bool PrepareOpen,
    bool FrameOpen,
    int OpenReaderCount,
    int OpenObservationCount,
    int RetainedPositionCount,
    int PendingTimelineCount,
    int RetryRequiredTimelineCount,
    bool Closing,
    bool Closed)
{
    public bool HasPendingTimelineWork =>
        PendingTimelineCount != 0 || RetryRequiredTimelineCount != 0;

    public bool WaitingForReaders => OpenReaderCount != 0 || RetainedPositionCount != 0;
}
