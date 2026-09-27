using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Single-coordinator page-stream state machine. Fault ingestion is thread-safe; exactly one
/// caller may advance <see cref="Update"/> at a time.
/// </summary>
internal sealed class PageStream : IDisposable, IAsyncDisposable
{
    internal const int DefaultMaxInFlightLoads = 256;
    internal const long DefaultMaxRetainedBytes = 16L * 1024 * 1024;
    internal const int DefaultMaxPendingFaultWords = 16 * 1024;
    internal const int DefaultMaxQueuedPages = 16 * 1024;

    private readonly record struct LoadOperation(uint Size, Task Completion);
    private readonly record struct CompletedStagedPage(
        uint PageID,
        PageLoadResult Result,
        Exception? Error);

    private readonly uint[] _faultInbox;
    private readonly HashSet<uint> _pendingLeafNodeIndices = new();
    private readonly Queue<uint> _queuedPages = new();
    private readonly HashSet<uint> _queuedPageSet = new();
    private readonly Dictionary<uint, LoadOperation> _loadingPages = new();
    private readonly HashSet<uint> _permanentlyFailedPages = new();
    private readonly Queue<CompletedStagedPage> _completedStagedPages = new();
    private readonly object _coordinatorGate = new();
    private readonly object _completionGate = new();
    private readonly object _faultGate = new();
    private readonly object _snapshotGate = new();
    private readonly ClusterMeshes _resources;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _disposalCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _shutdownCancellation = Task.CompletedTask;
    private readonly int _maxInFlightLoads;
    private readonly long _maxRetainedBytes;
    private readonly int _maxPendingFaultWords;
    private readonly int _maxQueuedPages;
    private long _loadingBytes;
    private int _pendingFaultWords;
    private ulong _pendingReportedFaults;
    private ulong _pendingStoredFaults;
    private ulong _pendingDroppedFaults;
    private ulong _reportedFaultCount;
    private ulong _storedFaultCount;
    private ulong _droppedFaultCount;
    private ulong _totalDroppedFaultCount;
    private uint _uniqueFaultLeafCount;
    private uint _knownFaultLeafCount;
    private uint _stagedPageCount;
    private uint _failedPageCount;
    private uint _backpressuredPageCount;
    private ulong _totalLoadFailureCount;
    private ulong _totalBackpressuredPageCount;
    private ulong _updateRevision;
    private ulong _failureSequence;
    private PageStreamFailure? _lastFailure;
    private PageStreamSnapshot _snapshot;
    private PageStreamSnapshot? _disposeTerminalSnapshot;
    private int _lifecycleState;
    private int _disposePendingLoads;
    private int _disposeSynchronousCleanupComplete;
    private int _disposeCompletionPublished;
    private int _resourcesAttached;

    public PageStreamSnapshot CaptureSnapshot()
    {
        lock (_snapshotGate)
            return _snapshot;
    }

    public PageStream(ClusterMeshes resources)
        : this(
            resources,
            DefaultMaxInFlightLoads,
            DefaultMaxRetainedBytes,
            DefaultMaxPendingFaultWords,
            DefaultMaxQueuedPages)
    {
    }

    internal PageStream(
        ClusterMeshes resources,
        int maxInFlightLoads = DefaultMaxInFlightLoads,
        long maxRetainedBytes = DefaultMaxRetainedBytes,
        int maxPendingFaultWords = DefaultMaxPendingFaultWords,
        int maxQueuedPages = DefaultMaxQueuedPages)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        if (maxInFlightLoads <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInFlightLoads));
        if (maxRetainedBytes < MeshPageHeader.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetainedBytes),
                $"The retained-byte budget must fit one maximum-size page ({MeshPageHeader.MaxPageSize} bytes).");
        }
        if (maxPendingFaultWords <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFaultWords));
        if (maxQueuedPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedPages));

        _maxInFlightLoads = maxInFlightLoads;
        _maxRetainedBytes = maxRetainedBytes;
        _maxPendingFaultWords = maxPendingFaultWords;
        _maxQueuedPages = maxQueuedPages;
        _faultInbox = new uint[maxPendingFaultWords];
        _pendingLeafNodeIndices.EnsureCapacity(maxPendingFaultWords);
        _queuedPages.EnsureCapacity(maxQueuedPages);
        _queuedPageSet.EnsureCapacity(maxQueuedPages);
        _loadingPages.EnsureCapacity(maxInFlightLoads);
        _completedStagedPages.EnsureCapacity(maxInFlightLoads);
        PublishSnapshot(PageStreamLifecycle.Active);
        try
        {
            _resources.AttachPageStream();
            Volatile.Write(ref _resourcesAttached, 1);
        }
        catch
        {
            _shutdown.Dispose();
            throw;
        }
    }

    public void Push(PageFaultRead faults)
    {
        lock (_faultGate)
        {
            ThrowIfNotActive();
            if (faults.EpochId != _resources.EpochId)
            {
                throw new ArgumentException(
                    $"Page faults belong to Cluster epoch {faults.EpochId}, not {_resources.EpochId}.",
                    nameof(faults));
            }

            int acceptedCount = Math.Min(
                faults.LeafNodeIndices.Length,
                _maxPendingFaultWords - _pendingFaultWords);
            ulong accepted = checked((uint)acceptedCount);
            ulong dropped = faults.ReportedCount - accepted;
            if (acceptedCount != 0)
            {
                faults.LeafNodeIndices[..acceptedCount].CopyTo(
                    _faultInbox.AsSpan(_pendingFaultWords, acceptedCount));
                _pendingFaultWords = checked(_pendingFaultWords + acceptedCount);
            }

            _pendingReportedFaults = SaturatingAdd(_pendingReportedFaults, faults.ReportedCount);
            _pendingStoredFaults = SaturatingAdd(_pendingStoredFaults, accepted);
            _pendingDroppedFaults = SaturatingAdd(_pendingDroppedFaults, dropped);
        }
    }

    public void Update()
    {
        ThrowIfNotActive();
        if (!Monitor.TryEnter(_coordinatorGate))
            throw new InvalidOperationException("PageStream.Update cannot run concurrently.");

        try
        {
            ThrowIfNotActive();
            DrainFaultInbox();
            DrainCompleted(out uint stagedPages, out uint failedPages);
            QueueRequestedPages();
            failedPages = checked(failedPages + StartQueuedPageLoads());
            _stagedPageCount = stagedPages;
            _failedPageCount = failedPages;
            _totalLoadFailureCount = SaturatingAdd(_totalLoadFailureCount, failedPages);
            _updateRevision = SaturatingAdd(_updateRevision, 1);
            PublishSnapshot(PageStreamLifecycle.Active);
        }
        finally
        {
            Monitor.Exit(_coordinatorGate);
        }
    }

    private void DrainFaultInbox()
    {
        _uniqueFaultLeafCount = 0;
        _knownFaultLeafCount = 0;
        _backpressuredPageCount = 0;

        lock (_faultGate)
        {
            _pendingLeafNodeIndices.EnsureCapacity(
                checked(_pendingLeafNodeIndices.Count + _pendingFaultWords));
            _reportedFaultCount = _pendingReportedFaults;
            _storedFaultCount = _pendingStoredFaults;
            _droppedFaultCount = _pendingDroppedFaults;
            _pendingReportedFaults = 0;
            _pendingStoredFaults = 0;
            _pendingDroppedFaults = 0;

            foreach (uint leafNodeIndex in _faultInbox.AsSpan(0, _pendingFaultWords))
                _pendingLeafNodeIndices.Add(leafNodeIndex);
            _pendingFaultWords = 0;
        }

        if (_droppedFaultCount != 0)
            _totalDroppedFaultCount = SaturatingAdd(_totalDroppedFaultCount, _droppedFaultCount);
    }

    private void QueueRequestedPages()
    {
        if (_pendingLeafNodeIndices.Count == 0)
            return;

        _uniqueFaultLeafCount = checked((uint)_pendingLeafNodeIndices.Count);
        foreach (uint leafNodeIndex in _pendingLeafNodeIndices)
        {
            PageFaultResolution resolution = _resources.ResolvePageFault(leafNodeIndex);
            if (resolution.Kind == PageFaultResolutionKind.Unknown)
                continue;
            _knownFaultLeafCount++;
            QueuePageIfNeeded(resolution.PageId, resolution);
        }

        _pendingLeafNodeIndices.Clear();
    }

    private void QueuePageIfNeeded(uint pageID, in PageFaultResolution resolution)
    {
        if (resolution.Kind is PageFaultResolutionKind.Satisfied or PageFaultResolutionKind.Pending)
            return;

        if (_permanentlyFailedPages.Contains(pageID) ||
            _loadingPages.ContainsKey(pageID) ||
            _queuedPageSet.Contains(pageID))
        {
            return;
        }

        if (_queuedPageSet.Count >= _maxQueuedPages)
        {
            RecordQueueBackpressure();
            return;
        }

        _queuedPages.Enqueue(pageID);
        _queuedPageSet.Add(pageID);
    }

    private void RecordQueueBackpressure()
    {
        _backpressuredPageCount = checked(_backpressuredPageCount + 1);
        _totalBackpressuredPageCount = SaturatingAdd(_totalBackpressuredPageCount, 1);
    }

    private uint StartQueuedPageLoads()
    {
        uint failedPages = 0;
        int queuedCount = _queuedPages.Count;
        for (int i = 0; i < queuedCount; i++)
        {
            uint pageID = _queuedPages.Dequeue();
            _queuedPageSet.Remove(pageID);

            PageFaultResolution resolution = _resources.ResolvePage(pageID);
            if (resolution.Kind is PageFaultResolutionKind.Unknown or
                PageFaultResolutionKind.Satisfied or
                PageFaultResolutionKind.Pending)
                continue;
            if (_permanentlyFailedPages.Contains(pageID) ||
                _loadingPages.ContainsKey(pageID))
                continue;
            uint size = resolution.Size;

            if (_loadingPages.Count >= _maxInFlightLoads ||
                checked(_loadingBytes + size) > _maxRetainedBytes)
            {
                _queuedPages.Enqueue(pageID);
                _queuedPageSet.Add(pageID);
                continue;
            }

            StartStagedPageLoad(pageID, size);
        }

        return failedPages;
    }

    private void StartStagedPageLoad(uint pageID, uint size)
    {
        _loadingBytes = checked(_loadingBytes + size);
        _loadingPages[pageID] = new LoadOperation(size, Task.CompletedTask);
        try
        {
            Task completion = CompleteStagedLoadAsync(pageID, _shutdown.Token);
            _loadingPages[pageID] = new LoadOperation(size, completion);
        }
        catch (Exception ex)
        {
            PublishStagedCompleted(new CompletedStagedPage(pageID, default, ex));
        }
    }

    private async Task CompleteStagedLoadAsync(uint pageID, CancellationToken cancellationToken)
    {
        CompletedStagedPage completed;
        try
        {
            PageLoadResult result = await _resources
                .LoadPageIntoStagingAsync(pageID, cancellationToken)
                .ConfigureAwait(false);
            completed = new CompletedStagedPage(pageID, result, null);
        }
        catch (Exception ex)
        {
            completed = new CompletedStagedPage(pageID, default, ex);
        }
        PublishStagedCompleted(completed);
    }

    private void PublishStagedCompleted(CompletedStagedPage completed)
    {
        lock (_completionGate)
        {
            if (Volatile.Read(ref _lifecycleState) == 0)
            {
                _completedStagedPages.Enqueue(completed);
                return;
            }
        }

        if (Volatile.Read(ref _lifecycleState) == 1 &&
            Interlocked.Decrement(ref _disposePendingLoads) == 0)
        {
            TryFinishDisposal();
        }
    }

    private void DrainCompleted(out uint stagedPages, out uint failedPages)
    {
        stagedPages = 0;
        failedPages = 0;
        while (TryTakeStagedCompleted(out CompletedStagedPage completed))
        {
            if (_loadingPages.Remove(completed.PageID, out LoadOperation operation))
                _loadingBytes = checked(_loadingBytes - operation.Size);

            if (completed.Error is not null)
            {
                PageStreamFailureCode code;
                string message;
                if (completed.Error is ClusterPageSourceException sourceError)
                {
                    code = PageStreamFailureCode.SourceReadFailed;
                    message = sourceError.InnerException?.Message ?? sourceError.Message;
                }
                else if (completed.Error is InvalidDataException)
                {
                    code = PageStreamFailureCode.InvalidPayload;
                    message = completed.Error.Message;
                }
                else
                {
                    ExceptionDispatchInfo.Capture(completed.Error).Throw();
                    throw new UnreachableException();
                }

                RecordFailure(completed.PageID, code, message);
                failedPages++;
                continue;
            }

            switch (completed.Result)
            {
                case PageLoadResult.Staged:
                    stagedPages++;
                    break;
                case PageLoadResult.AlreadyTracked:
                    break;
                case PageLoadResult.Deferred:
                    if (_queuedPageSet.Count < _maxQueuedPages && _queuedPageSet.Add(completed.PageID))
                        _queuedPages.Enqueue(completed.PageID);
                    else
                        RecordQueueBackpressure();
                    break;
                case PageLoadResult.UnknownPage:
                    RecordFailure(
                        completed.PageID,
                        PageStreamFailureCode.UnknownPage,
                        $"Cluster page {completed.PageID} is no longer registered.");
                    failedPages++;
                    break;
                case PageLoadResult.NoCapacity:
                    RecordFailure(
                        completed.PageID,
                        PageStreamFailureCode.PermanentCapacityFailure,
                        $"Cluster page {completed.PageID} could not reserve GPU page-heap capacity.");
                    failedPages++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(completed.Result));
            }
        }

    }

    private bool TryTakeStagedCompleted(out CompletedStagedPage completed)
    {
        lock (_completionGate)
            return _completedStagedPages.TryDequeue(out completed);
    }

    private static ulong SaturatingAdd(ulong left, ulong right)
        => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private void RecordFailure(
        uint pageId,
        PageStreamFailureCode code,
        string message)
    {
        _failureSequence = SaturatingAdd(_failureSequence, 1);
        _lastFailure = new PageStreamFailure(
            _failureSequence,
            pageId,
            code,
            message);
        if (code != PageStreamFailureCode.SourceReadFailed)
            _permanentlyFailedPages.Add(pageId);
    }

    private void PublishSnapshot(PageStreamLifecycle lifecycle)
        => WriteSnapshot(CreateSnapshot(lifecycle, CurrentWork()));

    private void WriteSnapshot(PageStreamSnapshot snapshot)
    {
        lock (_snapshotGate)
            _snapshot = snapshot;
    }

    private PageStreamSnapshot CreateSnapshot(
        PageStreamLifecycle lifecycle,
        PageStreamWorkSnapshot work)
        => CreateSnapshot(
            lifecycle,
            work,
            new PageStreamTotalsSnapshot(
                _totalDroppedFaultCount,
                _totalLoadFailureCount,
                _totalBackpressuredPageCount));

    private PageStreamSnapshot CreateSnapshot(
        PageStreamLifecycle lifecycle,
        PageStreamWorkSnapshot work,
        PageStreamTotalsSnapshot totals)
        => new(
            _resources.EpochId,
            lifecycle,
            _updateRevision,
            new PageStreamUpdateSnapshot(
                _reportedFaultCount,
                _storedFaultCount,
                _droppedFaultCount,
                _uniqueFaultLeafCount,
                _knownFaultLeafCount,
                _stagedPageCount,
                _failedPageCount,
                _backpressuredPageCount),
            totals,
            work,
            _lastFailure);

    private PageStreamWorkSnapshot CurrentWork()
        => new(
            _queuedPageSet.Count,
            _loadingPages.Count,
            _loadingBytes,
            _permanentlyFailedPages.Count);

    private void ThrowIfNotActive()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _lifecycleState) != 0,
            this);

    public void Dispose()
    {
        if (Volatile.Read(ref _lifecycleState) != 0)
            return;

        lock (_coordinatorGate)
        {
            lock (_completionGate)
            {
                lock (_faultGate)
                {
                    if (Volatile.Read(ref _lifecycleState) != 0)
                        return;

                    int completedCount = _completedStagedPages.Count;
                    if (completedCount > _loadingPages.Count)
                    {
                        throw new InvalidOperationException(
                            "Completed Cluster page loads exceed the tracked in-flight load count.");
                    }

                    int pendingLoads = _loadingPages.Count - completedCount;
                    var finalTotals = new PageStreamTotalsSnapshot(
                        SaturatingAdd(_totalDroppedFaultCount, _pendingDroppedFaults),
                        _totalLoadFailureCount,
                        _totalBackpressuredPageCount);
                    PageStreamSnapshot disposing = CreateSnapshot(
                        PageStreamLifecycle.Disposing,
                        CurrentWork(),
                        finalTotals);
                    PageStreamSnapshot terminal = CreateSnapshot(
                        PageStreamLifecycle.Disposed,
                        default,
                        finalTotals);

                    _disposePendingLoads = pendingLoads;
                    _disposeTerminalSnapshot = terminal;
                    _totalDroppedFaultCount = finalTotals.DroppedFaults;
                    Volatile.Write(ref _lifecycleState, 1);
                    WriteSnapshot(disposing);

                    _pendingFaultWords = 0;
                    _pendingReportedFaults = 0;
                    _pendingStoredFaults = 0;
                    _pendingDroppedFaults = 0;
                    _pendingLeafNodeIndices.Clear();
                    _queuedPages.Clear();
                    _queuedPageSet.Clear();
                    _completedStagedPages.Clear();
                    _loadingPages.Clear();
                    _loadingBytes = 0;
                    _permanentlyFailedPages.Clear();
                }
            }
        }

        Task cancellation;
        try
        {
            cancellation = _shutdown.CancelAsync();
        }
        catch (Exception error)
        {
            cancellation = Task.FromException(error);
        }
        Volatile.Write(ref _shutdownCancellation, cancellation);
        if (!cancellation.IsCompleted)
        {
            _ = cancellation.ContinueWith(
                static (_, state) => ((PageStream)state!).TryFinishDisposal(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        Volatile.Write(ref _disposeSynchronousCleanupComplete, 1);
        TryFinishDisposal();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _disposalCompletion.Task.ConfigureAwait(false);
    }

    private void TryFinishDisposal()
    {
        Task cancellation = Volatile.Read(ref _shutdownCancellation);
        if (Volatile.Read(ref _disposeSynchronousCleanupComplete) == 0 ||
            Volatile.Read(ref _disposePendingLoads) != 0 ||
            !cancellation.IsCompleted ||
            Interlocked.CompareExchange(ref _disposeCompletionPublished, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // CancelAsync captures callback exceptions in its task. Loader callbacks are not
            // allowed to strand ownership release or replace the load operation's own result.
            _ = cancellation.Exception;
            try { _shutdown.Dispose(); }
            catch { }

            PageStreamSnapshot terminal = _disposeTerminalSnapshot ??
                throw new InvalidOperationException("PageStream disposal has no prepared terminal snapshot.");
            if (Volatile.Read(ref _resourcesAttached) != 0)
            {
                _resources.DetachPageStream();
                Volatile.Write(ref _resourcesAttached, 0);
            }
            WriteSnapshot(terminal);
            Volatile.Write(ref _lifecycleState, 2);
            _disposalCompletion.TrySetResult();
        }
        catch (Exception error)
        {
            _disposalCompletion.TrySetException(error);
        }
    }

}
