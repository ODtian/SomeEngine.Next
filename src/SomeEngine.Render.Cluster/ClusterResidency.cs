using System.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Cluster;

/// <summary>Physical bounds and shutdown policy for Cluster geometry residency.</summary>
public sealed record ClusterResidencyOptions
{
    public uint PageHeapCapacityBytes { get; init; } = 64u * 1024 * 1024;

    public ulong BvhCapacityBytes { get; init; } = 64ul * 1024 * 1024;

    public int PageFaultCapacity { get; init; } = 16 * 1024;

    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfZero(PageHeapCapacityBytes);
        if (PageHeapCapacityBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageHeapCapacityBytes),
                "A managed Cluster page-heap mapping cannot exceed Int32.MaxValue bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfZero(BvhCapacityBytes);
        if (BvhCapacityBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BvhCapacityBytes),
                "A managed Cluster BVH mapping cannot exceed Int32.MaxValue bytes.");
        }

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
    Buffer PageHeap,
    Buffer Bvh,
    int PageFaultCapacity);

internal readonly record struct ClusterResidencyProviderSnapshot(
    ClusterMeshesSnapshot Meshes,
    PageStreamSnapshot Streaming);

/// <summary>
/// Owns Cluster page-heap and BVH storage plus CPU residency machinery. It performs no frame or
/// completion coordination; callers must prove the resources are quiescent before publication.
/// </summary>
internal sealed class ClusterResidency : IDisposable, IAsyncDisposable
{
    private readonly Buffer _pageHeapBuffer;
    private readonly Buffer _bvhBuffer;
    private readonly ClusterMeshes _meshes;
    private readonly PageStream _stream;
    private readonly PageFaults _faults;
    private readonly int _pageFaultCapacity;
    private readonly TimeSpan _disposeTimeout;
    private readonly object _disposeGate = new();
    private Task? _streamDisposal;
    private Exception? _meshCleanupFailure;
    private bool _meshCleanupAttempted;
    private bool _bvhBufferDestroyed;
    private bool _pageHeapBufferDestroyed;
    private int _lifecycleState;

    internal ClusterResidency(
        IGraphicsBackend backend,
        Device device,
        RenderTimeline timeline,
        ClusterResidencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (!ReferenceEquals(timeline.Device, device))
        {
            throw new ArgumentException(
                "Cluster residency and its render timeline must use the same device domain.",
                nameof(timeline));
        }
        ClusterResidencyOptions selected = options ?? new ClusterResidencyOptions();
        selected.Validate();

        Buffer? pageHeapBuffer = null;
        Buffer? bvhBuffer = null;
        MappedClusterPageStorage? pageStorage = null;
        MappedClusterBvhStorage? bvhStorage = null;
        ClusterMeshes? meshes = null;
        PageStream? stream = null;
        try
        {
            pageHeapBuffer = backend.CreateBuffer(
                device,
                new BufferDesc(
                    selected.PageHeapCapacityBytes,
                    BufferUsages.ShaderRead | BufferUsages.CopySource,
                    "Cluster page heap"),
                MemoryType.Upload);
            pageStorage = new MappedClusterPageStorage(
                backend,
                pageHeapBuffer,
                checked((int)selected.PageHeapCapacityBytes));

            bvhBuffer = backend.CreateBuffer(
                device,
                new BufferDesc(
                    selected.BvhCapacityBytes,
                    BufferUsages.ShaderRead | BufferUsages.CopySource,
                    "Cluster BVH"),
                MemoryType.Upload);
            bvhStorage = new MappedClusterBvhStorage(
                backend,
                bvhBuffer,
                checked((int)selected.BvhCapacityBytes));

            meshes = new ClusterMeshes(
                selected.PageHeapCapacityBytes,
                residency: null,
                pageStorage,
                bvhStorage);
            pageStorage = null;
            bvhStorage = null;
            stream = new PageStream(meshes);

            _pageHeapBuffer = pageHeapBuffer;
            _bvhBuffer = bvhBuffer;
            _meshes = meshes;
            _stream = stream;
            _faults = new PageFaults(meshes.EpochId, selected.PageFaultCapacity);
            _pageFaultCapacity = selected.PageFaultCapacity;
            _disposeTimeout = selected.DisposeTimeout;
            Timeline = timeline;
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

            TryDestroy(bvhBuffer);
            TryDestroy(pageHeapBuffer);
            throw;
        }
    }

    internal RenderTimeline Timeline { get; }

    internal ClusterEpochId EpochId => _meshes.EpochId;

    internal TimeSpan ShutdownTimeout => _disposeTimeout;

    internal bool IsRegistrationOwner => _meshes.IsRegistrationOwner;

    internal ClusterResidencyBinding GetBinding()
    {
        ThrowIfNotActive();
        return new ClusterResidencyBinding(
            _pageHeapBuffer,
            _bvhBuffer,
            _pageFaultCapacity);
    }

    internal bool IsDisposed => Volatile.Read(ref _lifecycleState) == 2;

    internal ValueTask<ClusterMeshRegistrationResult> RegisterMeshAsync(
        Mesh mesh,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotActive();
        return _meshes.RegisterMeshAsync(mesh, cancellationToken);
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
        _stream.Update();
    }

    /// <summary>
    /// Publishes pending roots, pages, and retirements under the residency timeline's linear
    /// prepare capability.
    /// </summary>
    internal bool PublishPending()
    {
        ThrowIfNotActive();
        return _meshes.PublishPending();
    }

    internal bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        ThrowIfNotActive();
        return _meshes.TryGetPublishedRoot(mesh, out root);
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
            meshes.PublishedMeshCount);
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

            ReleaseBuffer(_bvhBuffer, ref _bvhBufferDestroyed);
            ReleaseBuffer(_pageHeapBuffer, ref _pageHeapBufferDestroyed);

            if (failures is not null)
            {
                throw new AggregateException(
                    "Cluster residency cleanup completed with failures.",
                    failures);
            }
            Volatile.Write(ref _lifecycleState, 2);

            void ReleaseBuffer(Buffer buffer, ref bool destroyed)
            {
                if (destroyed)
                    return;
                try
                {
                    buffer.Dispose();
                    destroyed = true;
                }
                catch (Exception error)
                {
                    // A live handle remains owned by this residency and is retried by the next
                    // Shutdown call.
                    AddFailure(error);
                }
            }

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
                $"Cluster mesh registration did not release final BVH storage within {timeout}.",
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

    private static void TryDestroy(Buffer? buffer)
    {
        if (buffer is null)
            return;
        try
        {
            buffer.Dispose();
        }
        catch
        {
        }
    }
}
