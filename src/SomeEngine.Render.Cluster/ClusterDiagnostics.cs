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
    GeometryChanges,
    Disposal,
}

public sealed record ClusterCleanupError(
    ulong Sequence,
    ClusterCleanupFailureKind Kind,
    string ErrorType,
    string Message);

public sealed record ClusterResidencyDiagnostics(
    bool HasChanges,
    uint RegisteredPages,
    uint ResidentPages,
    uint MissingPages,
    int UploadingPages,
    int EvictingPages,
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

internal readonly record struct ClusterPageDiagnostics(
    uint Registered,
    uint Resident,
    uint Missing,
    int UncompletedLoads,
    int UncompletedEvictions,
    uint TotalCompletedEvictions);

internal readonly record struct ClusterHeapDiagnostics(
    uint CapacityBytes,
    uint UsedBytes,
    uint FreeBytes,
    uint LargestFreeBlockBytes,
    int FreeBlockCount);

internal readonly record struct ClusterResidencyMemoryDiagnostics(
    long GpuUsedBytes);

internal enum ClusterCleanupStage : byte
{
    Registration,
    PageLoad,
    GeometryChanges,
    Disposal,
}

internal readonly record struct ClusterCleanupFailure(
    ulong Sequence,
    ClusterCleanupStage Stage,
    string ErrorType,
    string Message);

internal readonly record struct ClusterMeshesDiagnostics(
    ClusterEpochId EpochId,
    ClusterLifecycle Lifecycle,
    ulong ManagerStateRevision,
    ClusterPageDiagnostics Pages,
    ClusterHeapDiagnostics Heap,
    bool HasChanges,
    ClusterResidencyMemoryDiagnostics Residency,
    int RegisteredMeshCount,
    int PublishedMeshCount,
    int ActivePageStreams,
    ClusterCleanupFailure? LastCleanupFailure);

internal enum PageFaultResolutionKind : byte
{
    Unknown,
    Satisfied,
    UploadQueued,
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

internal readonly record struct PageStreamUpdateDiagnostics(
    ulong ReportedFaults,
    ulong StoredFaults,
    ulong DroppedFaults,
    uint UniqueLeafNodeIndices,
    uint KnownLeafNodeIndices,
    uint StagedPages,
    uint FailedPages,
    uint BackpressuredPages);

internal readonly record struct PageStreamTotalsDiagnostics(
    ulong DroppedFaults,
    ulong LoadFailures,
    ulong BackpressuredPages);

internal readonly record struct PageStreamWorkDiagnostics(
    int QueuedPages,
    int InFlightPages,
    long InFlightBytes,
    int PermanentlyFailedPages);

internal readonly record struct PageStreamDiagnostics(
    ClusterEpochId EpochId,
    PageStreamLifecycle Lifecycle,
    ulong UpdateRevision,
    PageStreamUpdateDiagnostics LastUpdate,
    PageStreamTotalsDiagnostics Totals,
    PageStreamWorkDiagnostics Work,
    PageStreamFailure? LastFailure);
