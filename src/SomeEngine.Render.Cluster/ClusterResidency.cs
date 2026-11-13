using System.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Cluster;

/// <summary>Physical bounds and shutdown policy for Cluster geometry residency.</summary>
public sealed record ClusterResidencyOptions
{
    public uint PageHeapCapacityBytes { get; init; } = 256u * 1024 * 1024;

    public ulong BvhCapacityBytes { get; init; } = 64ul * 1024 * 1024;

    public int PageFaultCapacity { get; init; } = 16 * 1024;

    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfZero(PageHeapCapacityBytes);
        ArgumentOutOfRangeException.ThrowIfZero(BvhCapacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageFaultCapacity);
        if (DisposeTimeout <= TimeSpan.Zero || DisposeTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DisposeTimeout),
                "Cluster residency disposal requires a finite positive timeout.");
        }
    }
}

internal readonly record struct ClusterResidencyBinding(
    BufferDesc PageHeapDescription,
    BufferDesc BvhDescription,
    int PageFaultCapacity,
    uint GeometryPageCount,
    BufferRange[] ResidentPageRanges,
    BufferRange[] BvhRanges,
    ClusterResidentGeometry? Changes);

internal readonly record struct ClusterGeometryBuffers(
    GraphBufferId PageHeap, GraphBufferId Bvh, GraphPassId PageUpload, GraphPassId BvhUpload);

internal sealed record ClusterResidentGeometry(
    ulong Sequence,
    ClusterMeshChanges Meshes);

internal readonly record struct ClusterResidencyDetails(
    ClusterMeshesDiagnostics Meshes,
    PageStreamDiagnostics Streaming);

/// <summary>
/// Owns device-local Cluster page-heap and BVH resources plus their CPU staging machinery.
/// </summary>
internal sealed class ClusterResidency : IDisposable, IAsyncDisposable
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    internal ClusterGeometryBuffers GeometryBuffers { get; private set; }
    private readonly BufferDesc _pageHeapDescription;
    private readonly BufferDesc _bvhDescription;
    private readonly ClusterMeshes _meshes;
    private readonly PageStream _stream;
    private readonly PageFaults _faults;
    private readonly int _pageFaultCapacity;
    private readonly TimeSpan _disposeTimeout;
    private readonly object _disposeGate = new();
    private Task? _streamDisposal;
    private Exception? _meshCleanupFailure;
    private bool _meshCleanupAttempted;
    private int _lifecycleState;
    private ulong _geometryChangeSequence;
    private ClusterResidentGeometry? _geometryChanges;
    private readonly HashSet<Mesh> _unusedMeshes = [];

    private float _streamingQuality = 1.0f;
    private uint _overBudgetFrames;
    private uint _underBudgetFrames;

    internal float StreamingQuality => _streamingQuality;
    internal ReadOnlyMemory<ClusterLodChange> LodChanges => _geometryChanges?.Meshes.LodChanges ?? [];
    internal uint GeometryPageCount => _meshes.GeometryPageCount;
    internal uint MaximumRasterBatchesPerCluster => _meshes.MaximumRasterBatchesPerCluster;
    internal int HierarchyLevels => _meshes.HierarchyLevels;

    internal ClusterResidency(global::SomeEngine.RenderGraph.RenderGraph graph, ClusterResidencyOptions? options = null)
    {
        _graph = graph;
        ClusterResidencyOptions selected = options ?? new ClusterResidencyOptions();
        selected.Validate();

        SparseClusterPageStorage? pageStorage = null;
        SparseClusterBvhStorage? bvhStorage = null;
        ClusterMeshes? meshes = null;
        PageStream? stream = null;
        try
        {
            pageStorage = new SparseClusterPageStorage(selected.PageHeapCapacityBytes);
            bvhStorage = new SparseClusterBvhStorage(selected.BvhCapacityBytes);

            meshes = new ClusterMeshes(
                selected.PageHeapCapacityBytes,
                residency: null,
                pageStorage,
                bvhStorage,
                selected.BvhCapacityBytes);
            pageStorage = null;
            bvhStorage = null;
            stream = new PageStream(meshes);

            _pageHeapDescription = new BufferDesc(selected.PageHeapCapacityBytes,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Cluster page heap");
            _bvhDescription = new BufferDesc(selected.BvhCapacityBytes,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Cluster BVH");
            _meshes = meshes;
            _stream = stream;
            _faults = new PageFaults(meshes.EpochId, selected.PageFaultCapacity);
            _pageFaultCapacity = selected.PageFaultCapacity;
            _disposeTimeout = selected.DisposeTimeout;
            InstallGeometryBuffers();
        }
        catch
        {
            if (stream is not null)
            {
                try
                {
                    stream.Dispose();
                    stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            if (meshes is not null)
            {
                try
                {
                    _ = meshes.DisposeWithFailure();
                }
                catch
                {
                }
            }
            else
            {
                TryDispose(bvhStorage);
                TryDispose(pageStorage);
            }

            throw;
        }
    }

    internal ClusterEpochId EpochId => _meshes.EpochId;

    internal TimeSpan ShutdownTimeout => _disposeTimeout;

    internal bool IsRegistrationContext => _meshes.IsRegistrationContext;

    internal int PageFaultCapacity => _pageFaultCapacity;

    private void InstallGeometryBuffers()
    {
        using var edit = _graph.BeginEdit();
        GraphBufferId pages = edit.CreatePersistentBuffer(_pageHeapDescription);
        GraphBufferId bvh = edit.CreatePersistentBuffer(_bvhDescription);
        GraphPassId pageUpload = edit.AddBufferUploadPass(pages);
        GraphPassId bvhUpload = edit.AddBufferUploadPass(bvh);
        edit.Commit();
        GeometryBuffers = new(pages, bvh, pageUpload, bvhUpload);
    }

    internal ClusterResidencyBinding GetBinding()
    {
        ThrowIfNotActive();
        return new ClusterResidencyBinding(
            _pageHeapDescription,
            _bvhDescription,
            _pageFaultCapacity,
            _meshes.GeometryPageCount,
            _geometryChanges?.Meshes.ResidentPageRanges ?? _meshes.CaptureResidentPageRanges(),
            _meshes.CaptureBvhRanges(),
            _geometryChanges);
    }

    internal bool IsDisposed => Volatile.Read(ref _lifecycleState) == 2;

    internal ValueTask<ClusterMeshRegistrationResult[]> RegisterMeshesAsync(
        IReadOnlyList<ClusterMeshRequest> requests, CancellationToken cancellationToken = default)
    {
        ThrowIfNotActive();
        return _meshes.RegisterMeshesAsync(requests, cancellationToken);
    }

    internal async ValueTask LoadOpeningPagesAsync(
        IReadOnlyCollection<uint> lowestDetailPageIds,
        CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(
            lowestDetailPageIds,
            new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken },
            async (pageId, token) =>
            {
                PageLoadResult result = await _meshes.LoadPageIntoStagingAsync(pageId, token).ConfigureAwait(false);
                if (result is not (PageLoadResult.Staged or PageLoadResult.AlreadyTracked))
                    throw new InvalidOperationException(
                        $"Lowest-detail Cluster page {pageId} could not be loaded before mesh geometryChanges ({result}).");
            }).ConfigureAwait(false);
        // Coarse pages establish complete opening coverage. GPU visibility and
        // LOD selection request the required detail and its reduction groups.
    }

    internal bool IsMeshRegistered(Mesh mesh)
    {
        ThrowIfNotActive();
        return _meshes.IsMeshRegistered(mesh);
    }

    internal void KeepMesh(Mesh mesh) => _unusedMeshes.Remove(mesh);

    internal ClusterGeometry ReserveGpu(ClusterGpuBufferSizes sizes, string encoding, ulong revision, Bounds? bounds) =>
        _meshes.ReserveGpu(sizes, encoding, revision, bounds);

    internal ClusterGpuAllocation GetGpu(ClusterGeometry geometry) => _meshes.GetGpu(geometry);

    internal void ReleaseGpu(ClusterGeometry geometry) =>
        _meshes.ReleaseGpu(geometry, _graph.LastSubmittedFrame);
    internal void MarkMeshUnused(Mesh mesh) => _unusedMeshes.Add(mesh);

    internal void ReleaseUnusedMeshes()
    {
        if (_geometryChanges is not null) return;
        if (_unusedMeshes.Count != 0)
        {
            ulong frame = _graph.LastSubmittedFrame;
            foreach (Mesh mesh in _unusedMeshes) _meshes.RemoveMesh(mesh, frame);
            _unusedMeshes.Clear();
        }
        _meshes.RetireCompleted(_graph);
    }

    /// <summary>Advances page-fault ingestion and page IO without publishing GPU-visible data.</summary>
    internal void PumpStreaming()
    {
        ThrowIfNotActive();
        _meshes.RetireCompleted(_graph);
        if (_stream.Update() is ClusterStreamingDemand demand)
            UpdateStreamingQuality(demand);
    }

    /// <summary>Freezes one geometryChanges for the next RenderGraph frame without waiting on the GPU.</summary>
    internal bool PrepareChanges()
    {
        ThrowIfNotActive();
        _meshes.RetireCompleted(_graph);
        if (_geometryChanges is not null)
            return true;
        ClusterMeshChanges? meshes = _meshes.PrepareChanges();
        if (meshes is null)
            return false;
        _geometryChangeSequence = checked(_geometryChangeSequence + 1);
        _geometryChanges = new ClusterResidentGeometry(_geometryChangeSequence, meshes);
        return true;
    }

    internal void CommitChanges(
        ClusterResidentGeometry geometryChanges,
        ulong frameIdentity)
    {
        ThrowIfNotActive();
        ArgumentNullException.ThrowIfNull(geometryChanges);
        if (!ReferenceEquals(geometryChanges, _geometryChanges))
            throw new InvalidOperationException("The submitted Cluster residency geometryChanges is stale.");
        _meshes.CommitChanges(geometryChanges.Meshes, frameIdentity);
        _geometryChanges = null;
        _meshes.RetireCompleted(_graph);
    }

    internal bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        ThrowIfNotActive();
        if (_meshes.TryGetPublishedRoot(mesh, out root))
            return true;
        if (_geometryChanges is { } geometryChanges &&
            geometryChanges.Meshes.Bvh.RootMeshes.Any(candidate => ReferenceEquals(candidate, mesh)))
            return _meshes.TryGetRegisteredRoot(mesh, out root);
        root = 0;
        return false;
    }

    internal bool TryGetGeometry(Mesh mesh, out ClusterGeometry geometry)
    {
        if (TryGetPublishedRoot(mesh, out uint root))
        {
            geometry = _meshes.GetGeometry(mesh, root);
            return true;
        }
        geometry = default;
        return false;
    }

    internal int PublishedMeshCount
    {
        get
        {
            ThrowIfNotActive();
            return _meshes.PublishedMeshCount;
        }
    }

    internal ClusterMeshesDiagnostics CaptureMeshDiagnostics()
    {
        ThrowIfNotActive();
        return _meshes.CaptureDiagnostics();
    }

    internal void IngestFaults(ReadOnlySpan<byte> bytes)
    {
        ThrowIfNotActive();
        PageFaultRead faults = _faults.Read(bytes);
        _stream.Push(faults);
    }

    internal void IngestDemand(ReadOnlyMemory<ClusterPagePriority> demands, bool complete, ulong frameIdentity)
    {
        _stream.PushDemand(demands, complete, frameIdentity);
    }

    private void UpdateStreamingQuality(ClusterStreamingDemand demand)
    {
        if (!demand.IsComplete)
        {
            _overBudgetFrames = _underBudgetFrames = 0;
            return;
        }
        float load = demand.Capacity == 0 ? (demand.Bytes == 0 ? 0 : float.PositiveInfinity)
            : (float)((double)demand.Bytes / demand.Capacity);
        _overBudgetFrames = load > .85f ? Math.Min(_overBudgetFrames + 1, 2u) : 0;
        _underBudgetFrames = load < .70f ? Math.Min(_underBudgetFrames + 1, 30u) : 0;
        if (_overBudgetFrames >= 2) _streamingQuality *= .97f;
        else if (_underBudgetFrames >= 30) _streamingQuality *= 1.01f;
        _streamingQuality = Math.Clamp(_streamingQuality, .3f, 1.0f);
    }

    internal void CaptureDiagnostics(
        out ClusterResidencyDiagnostics residency,
        out ClusterMeshCacheDiagnostics meshCache)
    {
        ThrowIfNotActive();
        ClusterResidencyDetails diagnosticData = CaptureDiagnostics();
        ClusterMeshesDiagnostics meshes = diagnosticData.Meshes;
        PageStreamDiagnostics stream = diagnosticData.Streaming;
        ClusterPageLoadFailure? loadFailure = stream.LastFailure is null
            ? null
            : new ClusterPageLoadFailure(
                stream.LastFailure.Sequence,
                stream.LastFailure.PageId,
                stream.LastFailure.Code switch
                {
                    PageStreamFailureCode.SourceReadFailed => ClusterPageLoadFailureKind.SourceRead,
                    PageStreamFailureCode.InvalidPayload => ClusterPageLoadFailureKind.InvalidPayload,
                    PageStreamFailureCode.UnknownPage => ClusterPageLoadFailureKind.UnknownPage,
                    PageStreamFailureCode.PermanentCapacityFailure => ClusterPageLoadFailureKind.Capacity,
                    _ => throw new InvalidOperationException("Unknown Cluster page-load failure code."),
                },
                stream.LastFailure.Message);
        ClusterCleanupError? cleanupFailure = meshes.LastCleanupFailure is ClusterCleanupFailure failure
            ? new ClusterCleanupError(
                failure.Sequence,
                failure.Stage switch
                {
                    ClusterCleanupStage.Registration => ClusterCleanupFailureKind.Registration,
                    ClusterCleanupStage.PageLoad => ClusterCleanupFailureKind.PageLoad,
                    ClusterCleanupStage.GeometryChanges => ClusterCleanupFailureKind.GeometryChanges,
                    ClusterCleanupStage.Disposal => ClusterCleanupFailureKind.Disposal,
                    _ => throw new InvalidOperationException("Unknown Cluster cleanup stage."),
                },
                failure.ErrorType,
                failure.Message)
            : null;

        residency = new ClusterResidencyDiagnostics(
            meshes.HasChanges,
            meshes.Pages.Registered,
            meshes.Pages.Resident,
            meshes.Pages.Missing,
            meshes.Pages.UncompletedLoads,
            meshes.Pages.UncompletedEvictions,
            meshes.Heap.UsedBytes,
            meshes.Heap.FreeBytes,
            meshes.Residency.GpuUsedBytes,
            stream.Work.QueuedPages,
            stream.Work.InFlightPages,
            stream.Totals.DroppedFaults,
            stream.Totals.LoadFailures,
            stream.Totals.BackpressuredPages,
            loadFailure,
            cleanupFailure);
        meshCache = new ClusterMeshCacheDiagnostics(
            meshes.RegisteredMeshCount,
            meshes.PublishedMeshCount,
            meshes.ManagerStateRevision);
    }

    internal ClusterResidencyDetails CaptureDiagnostics()
        => new(
            _meshes.CaptureDiagnostics(),
            _stream.CaptureDiagnostics());

    public void Dispose()
        => Shutdown(_disposeTimeout);

    internal Task StopRegistrations()
        => _meshes.StopRegistrations();

    internal void StopStreaming(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task streamDisposal;
        lock (_disposeGate)
        {
            if (_lifecycleState == 2)
                return;
            if (_lifecycleState == 0)
            {
                _lifecycleState = 1;
                try
                {
                    _stream.Dispose();
                    _streamDisposal = _stream.DisposeAsync().AsTask();
                }
                catch
                {
                    _lifecycleState = 0;
                    throw;
                }
            }
            streamDisposal = _streamDisposal
                ?? throw new InvalidOperationException("Cluster streaming disposal was not started.");
        }

        try
        {
            streamDisposal.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Cluster streaming did not release its page IO within {timeout}.",
                error);
        }

    }

    internal void DestroyStorage()
    {
        lock (_disposeGate)
        {
            if (_lifecycleState == 2)
                return;
            if (_lifecycleState == 0 || _streamDisposal is null)
            {
                throw new InvalidOperationException(
                    "Cluster streaming must stop before residency storage is destroyed.");
            }
            if (!_streamDisposal.IsCompleted)
                throw new InvalidOperationException("Cluster streaming is still stopping.");
            _streamDisposal.GetAwaiter().GetResult();

            // Quiescence is a non-destructive precondition. If it is not satisfied, no mapping
            // or buffer is released and shutdown remains retryable.
            _meshes.EnsureReadyForDisposal();

            List<Exception>? failures = null;
            if (!_meshCleanupAttempted)
            {
                try
                {
                    _meshCleanupFailure = _meshes.DisposeWithFailure();
                    _meshCleanupAttempted = true;
                }
                catch (Exception error)
                {
                    // Preparation failures leave ClusterMeshes retryable. Only a returned
                    // cleanup failure is terminal because that owner has already committed its
                    // disposed state.
                    AddFailure(error);
                }
            }
            if (_meshCleanupFailure is not null)
                AddFailure(_meshCleanupFailure);
            if (!_meshCleanupAttempted)
            {
                throw new AggregateException(
                    "Cluster residency cleanup could not retire its mesh epoch.",
                    failures ?? []);
            }

            _unusedMeshes.Clear();

            if (failures is not null)
            {
                throw new AggregateException(
                    "Cluster residency cleanup completed with failures.",
                    failures);
            }
            Volatile.Write(ref _lifecycleState, 2);

            void AddFailure(Exception error)
            {
                failures ??= [];
                failures.Add(error);
            }
        }
    }

    private void Shutdown(TimeSpan timeout)
    {
        if (IsRegistrationContext)
        {
            throw new InvalidOperationException(
                "A Cluster registration cannot shut down its own residency epoch.");
        }
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        long started = Stopwatch.GetTimestamp();
        Task registrationIdle = StopRegistrations();
        try
        {
            registrationIdle.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Cluster mesh registration did not release BVH staging storage within {timeout}.",
                error);
        }

        TimeSpan remaining = Remaining(started, timeout);
        StopStreaming(remaining);
        DestroyStorage();
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        catch (Exception error)
        {
            return ValueTask.FromException(error);
        }
    }

    private void ThrowIfNotActive()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _lifecycleState) != 0, this);

    private static TimeSpan Remaining(long started, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"Cluster shutdown exceeded its {timeout} deadline.");
        return remaining;
    }

    private static void TryDispose(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
        }
    }

}
