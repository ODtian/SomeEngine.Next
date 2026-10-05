using System.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;
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
    BufferRange[] ResidentPageRanges,
    BufferRange BvhRange,
    ClusterResidencyPublication? Publication);

internal readonly record struct ClusterGeometryBuffers(
    GraphBufferId PageHeap, GraphBufferId Bvh, GraphPassId PageUpload, GraphPassId BvhUpload,
    GraphBufferId SourceClusterRanges, GraphPassId SourceRangeUpload);

internal sealed record ClusterResidencyPublication(
    ulong Sequence,
    ClusterMeshPublication Meshes);

internal readonly record struct ClusterResidencyProviderSnapshot(
    ClusterMeshesSnapshot Meshes,
    PageStreamSnapshot Streaming);

/// <summary>
/// Holds Cluster residency capacities, CPU mesh data, and streaming work.
/// </summary>
internal sealed class ClusterResidency : IDisposable, IAsyncDisposable
{
    private global::SomeEngine.RenderGraph.RenderGraph? _graph;
    internal ClusterGeometryBuffers GeometryBuffers { get; private set; }
    private readonly BufferDesc _pageHeapDescription;
    private readonly BufferDesc _bvhDescription;
    private readonly IGraphicsBackend _backend;
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
    private ulong _publicationSequence;
    private ClusterResidencyPublication? _activePublication;

    internal int HierarchyLevels => _meshes.HierarchyLevels;

    internal ClusterResidency(
        IGraphicsBackend backend,
        Device device,
        ClusterResidencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
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
                bvhStorage);
            pageStorage = null;
            bvhStorage = null;
            stream = new PageStream(meshes);

            _pageHeapDescription = new BufferDesc(selected.PageHeapCapacityBytes,
                BufferUsages.ShaderRead | BufferUsages.CopyDestination, "Cluster page heap");
            _bvhDescription = new BufferDesc(selected.BvhCapacityBytes,
                BufferUsages.ShaderRead | BufferUsages.CopyDestination, "Cluster BVH");
            _backend = backend;
            _meshes = meshes;
            _stream = stream;
            _faults = new PageFaults(meshes.EpochId, selected.PageFaultCapacity);
            _pageFaultCapacity = selected.PageFaultCapacity;
            _disposeTimeout = selected.DisposeTimeout;
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

    internal bool IsRegistrationOwner => _meshes.IsRegistrationOwner;

    internal int PageFaultCapacity => _pageFaultCapacity;

    internal void InstallGeometryBuffers(global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        if (ReferenceEquals(_graph, graph)) return;
        DevelopmentCheck.Require(_graph is null, "Cluster geometry buffers require one graph per residency epoch.");
        using var edit = graph.BeginEdit();
        GraphBufferId pages = edit.CreatePersistentBuffer(_pageHeapDescription);
        GraphBufferId bvh = edit.CreatePersistentBuffer(_bvhDescription);
        GraphPassId pageUpload = edit.AddBufferUploadPass(pages);
        GraphPassId bvhUpload = edit.AddBufferUploadPass(bvh);
        GraphBufferId sourceRanges = edit.CreatePersistentBuffer(new BufferDesc(
            checked(_bvhDescription.Size / ClusterBvh.NodeBytes * 8u),
            BufferUsages.ShaderRead | BufferUsages.CopyDestination, "Cluster source ranges"));
        GraphPassId sourceRangeUpload = edit.AddBufferUploadPass(sourceRanges);
        edit.Commit();
        GeometryBuffers = new ClusterGeometryBuffers(pages, bvh, pageUpload, bvhUpload, sourceRanges, sourceRangeUpload);
        _graph = graph;
    }

    internal ClusterResidencyBinding GetBinding()
    {
        ThrowIfNotActive();
        return new ClusterResidencyBinding(
            _pageHeapDescription,
            _bvhDescription,
            _pageFaultCapacity,
            _activePublication?.Meshes.ResidentPageRanges ?? _meshes.CaptureResidentPageRanges(),
            _meshes.CaptureBvhRange(),
            _activePublication);
    }

    internal bool IsDisposed => Volatile.Read(ref _lifecycleState) == 2;

    internal ValueTask<ClusterMeshRegistrationResult> RegisterMeshAsync(
        Mesh mesh,
        CancellationToken cancellationToken = default,
        SomeEngine.Assets.Pipeline.MeshPayloadSource? pageSource = null)
    {
        ThrowIfNotActive();
        return _meshes.RegisterMeshAsync(mesh, cancellationToken, pageSource);
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
                        $"Lowest-detail Cluster page {pageId} could not be loaded before mesh publication ({result}).");
            }).ConfigureAwait(false);
        // Coarse pages establish complete opening coverage. GPU visibility and
        // LOD selection request the required detail and its reduction groups.
    }

    internal bool IsMeshRegistered(Mesh mesh)
    {
        ThrowIfNotActive();
        return _meshes.IsMeshRegistered(mesh);
    }

    /// <summary>Advances page-fault ingestion and page IO without publishing GPU-visible data.</summary>
    internal void PumpStreaming()
    {
        ThrowIfNotActive();
        _meshes.RetireCompleted(_backend);
        _stream.Update();
    }

    /// <summary>Freezes one publication for the next RenderGraph frame without waiting on the GPU.</summary>
    internal bool PreparePublication()
    {
        ThrowIfNotActive();
        _meshes.RetireCompleted(_backend);
        if (_activePublication is not null)
            return true;
        ClusterMeshPublication? meshes = _meshes.PreparePublication();
        if (meshes is null)
            return false;
        _publicationSequence = checked(_publicationSequence + 1);
        _activePublication = new ClusterResidencyPublication(_publicationSequence, meshes);
        return true;
    }

    internal void CommitPublication(
        ClusterResidencyPublication publication,
        QueueCompletion[][] retirementCompletions)
    {
        ThrowIfNotActive();
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication, _activePublication))
            throw new InvalidOperationException("The submitted Cluster residency publication is stale.");
        _meshes.CommitPublication(publication.Meshes, retirementCompletions);
        _activePublication = null;
        _meshes.RetireCompleted(_backend);
    }

    internal void RetainPublication(ClusterResidencyPublication publication)
    {
        ThrowIfNotActive();
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication, _activePublication))
            throw new InvalidOperationException("The discarded Cluster residency publication is stale.");
    }

    internal uint GetSourceClusterCount(Mesh mesh) =>
        TryGetPublishedRoot(mesh, out uint root) ? _meshes.GetSourceClusterCount(root) : 0u;

    internal bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        ThrowIfNotActive();
        if (_meshes.TryGetPublishedRoot(mesh, out root))
            return true;
        if (_activePublication is { } publication &&
            publication.Meshes.Bvh.RootMeshes.Any(candidate => ReferenceEquals(candidate, mesh)))
            return _meshes.TryGetRegisteredRoot(mesh, out root);
        root = 0;
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

    internal ClusterMeshesSnapshot CaptureMeshesSnapshot()
    {
        ThrowIfNotActive();
        return _meshes.CaptureSnapshot();
    }

    internal void IngestFaults(ReadOnlySpan<byte> bytes)
    {
        ThrowIfNotActive();
        PageFaultRead faults = _faults.Read(bytes);
        _stream.Push(faults);
    }

    internal void ReportLeafUsage(ReadOnlySpan<uint> leafNodeIndices)
    {
        ThrowIfNotActive();
        _meshes.ReportLeafUsage(leafNodeIndices);
    }

    internal void CaptureDiagnostics(
        out ClusterResidencyDiagnostics residency,
        out ClusterMeshCacheDiagnostics meshCache)
    {
        ThrowIfNotActive();
        ClusterResidencyProviderSnapshot snapshot = CaptureSnapshot();
        ClusterMeshesSnapshot meshes = snapshot.Meshes;
        PageStreamSnapshot stream = snapshot.Streaming;
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
                    ClusterCleanupStage.Publication => ClusterCleanupFailureKind.Publication,
                    ClusterCleanupStage.Disposal => ClusterCleanupFailureKind.Disposal,
                    _ => throw new InvalidOperationException("Unknown Cluster cleanup stage."),
                },
                failure.ErrorType,
                failure.Message)
            : null;

        residency = new ClusterResidencyDiagnostics(
            meshes.HasPendingPublication,
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

    internal ClusterResidencyProviderSnapshot CaptureSnapshot()
        => new(
            _meshes.CaptureSnapshot(),
            _stream.CaptureSnapshot());

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
        if (IsRegistrationOwner)
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
