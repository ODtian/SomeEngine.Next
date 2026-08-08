namespace SomeEngine.Render.Cluster;

public enum ClusterPageLoadFailureKind : byte
{
    SourceRead,
    InvalidPayload,
    UnknownPage,
    Capacity,
}

public sealed record ClusterPageLoadFailure(
    ulong Sequence,
    uint PageId,
    ClusterPageLoadFailureKind Kind,
    string Message);

public enum ClusterCleanupFailureKind : byte
{
    Registration,
    PageLoad,
    Publication,
    Disposal,
}

public sealed record ClusterCleanupError(
    ulong Sequence,
    ClusterCleanupFailureKind Kind,
    string ErrorType,
    string Message);

public sealed record ClusterResidencyDiagnostics(
    bool HasPendingPublication,
    uint RegisteredPages,
    uint ResidentPages,
    uint MissingPages,
    int PendingPageLoads,
    int PendingPageEvictions,
    uint PageHeapUsedBytes,
    uint PageHeapFreeBytes,
    long GpuReservedBytes,
    int QueuedPageLoads,
    int ActivePageLoads,
    ulong DroppedPageFaults,
    ulong PageLoadFailures,
    ulong BackpressuredPages,
    ClusterPageLoadFailure? LastPageLoadFailure,
    ClusterCleanupError? LastCleanupError);

internal enum ClusterLifecycle : byte
{
    Active,
    Disposed,
}

internal readonly record struct ClusterPageStateSnapshot(
    uint Registered,
    uint Resident,
    uint Missing,
    int UncompletedLoads,
    int UncompletedEvictions,
    uint TotalCompletedEvictions);

internal readonly record struct ClusterHeapSnapshot(
    uint CapacityBytes,
    uint UsedBytes,
    uint FreeBytes,
    uint LargestFreeBlockBytes,
    int FreeBlockCount);

internal readonly record struct ClusterResidencySnapshot(
    long GpuUsedBytes);

internal enum ClusterCleanupStage : byte
{
    Registration,
    PageLoad,
    Publication,
    Disposal,
}

internal readonly record struct ClusterCleanupFailure(
    ulong Sequence,
    ClusterCleanupStage Stage,
    string ErrorType,
    string Message);

internal readonly record struct ClusterMeshesSnapshot(
    ClusterEpochId EpochId,
    ClusterLifecycle Lifecycle,
    ulong ManagerStateRevision,
    ClusterPageStateSnapshot Pages,
    ClusterHeapSnapshot Heap,
    bool HasPendingPublication,
    ClusterResidencySnapshot Residency,
    int RegisteredMeshCount,
    int PublishedMeshCount,
    int ActivePageStreams,
    ClusterCleanupFailure? LastCleanupFailure);

internal enum PageFaultResolutionKind : byte
{
    Unknown,
    Satisfied,
    Pending,
    NeedsLoad,
}

internal readonly record struct PageFaultResolution(
    PageFaultResolutionKind Kind,
    uint PageId,
    uint Size);

internal enum PageStreamLifecycle : byte
{
    Active,
    Disposing,
    Disposed,
}

internal enum PageStreamFailureCode : byte
{
    SourceReadFailed,
    InvalidPayload,
    UnknownPage,
    PermanentCapacityFailure,
}

internal sealed record PageStreamFailure(
    ulong Sequence,
    uint PageId,
    PageStreamFailureCode Code,
    string Message);

internal readonly record struct PageStreamUpdateSnapshot(
    ulong ReportedFaults,
    ulong StoredFaults,
    ulong DroppedFaults,
    uint UniqueLeafNodeIndices,
    uint KnownLeafNodeIndices,
    uint StagedPages,
    uint FailedPages,
    uint BackpressuredPages);

internal readonly record struct PageStreamTotalsSnapshot(
    ulong DroppedFaults,
    ulong LoadFailures,
    ulong BackpressuredPages);

internal readonly record struct PageStreamWorkSnapshot(
    int QueuedPages,
    int InFlightPages,
    long InFlightBytes,
    int PermanentlyFailedPages);

internal readonly record struct PageStreamSnapshot(
    ClusterEpochId EpochId,
    PageStreamLifecycle Lifecycle,
    ulong UpdateRevision,
    PageStreamUpdateSnapshot LastUpdate,
    PageStreamTotalsSnapshot Totals,
    PageStreamWorkSnapshot Work,
    PageStreamFailure? LastFailure);
