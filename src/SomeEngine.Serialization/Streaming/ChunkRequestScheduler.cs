using System.Diagnostics;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Serialization.Streaming;

public readonly record struct ChunkRequestOptions(int Priority = 0, DateTimeOffset? Deadline = null);

/// <summary>
/// Trusted pre-I/O size declaration used to admit both the stored input and decoded output before
/// the loader is allowed to allocate or decompress them.
/// </summary>
public readonly record struct ChunkLoadEstimate(long StoredBytes, long DecodedBytes)
{
    /// <summary>Final allocation capacity when alignment requires more storage than payload bytes.</summary>
    public long? BufferBytes { get; init; }
}

/// <summary>
/// Allocation-free pin over an already resident chunk. Copies carry the same scheduler token;
/// disposing any copy releases that logical pin once and invalidates every other copy.
/// </summary>
public struct ResidentChunkLease : IDisposable
{
    private ChunkRequestScheduler? _owner;
    private readonly ulong _key;
    private readonly long _generation;
    private readonly long _pinToken;
    private int _disposed;

    internal ResidentChunkLease(ChunkRequestScheduler owner, ulong key, long generation, long pinToken)
    {
        _owner = owner;
        _key = key;
        _generation = generation;
        _pinToken = pinToken;
    }

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ChunkRequestScheduler? owner = Volatile.Read(ref _owner);
            if (owner is null || Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ResidentChunkLease));
            return owner.GetPinnedMemory(_key, _generation, _pinToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _owner, null)?.Release(_key, _generation, _pinToken);
    }

    /// <summary>Creates an independent content branch sharing the acquired pages.</summary>
    public PageBuffer DuplicatePages() => GetScheduler().DuplicatePinnedPages(_key, _generation, _pinToken);

    /// <summary>Retains selected pages independently of this pin, the cache and the document.</summary>
    public PageRange<byte> RetainPages(long offset = 0, long? length = null)
        => GetScheduler().RetainPinnedPages(_key, _generation, _pinToken, offset, length);

    private ChunkRequestScheduler GetScheduler()
    {
        ChunkRequestScheduler? scheduler = Volatile.Read(ref _owner);
        ObjectDisposedException.ThrowIf(scheduler is null || Volatile.Read(ref _disposed) != 0, this);
        return scheduler;
    }
}

/// <summary>
/// Priority queue, in-flight deduplication, cancellation isolation, and pinned LRU residency for
/// decoded chunks. A caller cancellation only abandons that waiter; shared I/O continues for others.
/// </summary>
public sealed class ChunkRequestScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<ulong, CancellationToken, ValueTask<ChunkLease>> _loader;
    private readonly Func<ulong, ResidencyReservation, CancellationToken, ValueTask<ChunkLease>>? _pageLoader;
    private readonly Func<ValueTask>? _disposeLoader;
    private readonly Func<ulong, CancellationToken, ValueTask<ChunkLoadEstimate>> _estimator;
    private readonly bool _loaderReportsStoredBytes;
    private readonly Dictionary<ulong, CacheEntry> _entries = [];
    private readonly PriorityQueue<QueuedLoad, (long Priority, long Sequence)> _queue = new();
    private readonly LinkedList<CacheEntry> _lru = [];
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _budgetSignalGate = new();
    private TaskCompletionSource _budgetChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly int _maxQueuedRequests;
    private readonly long _decodedBudget;
    private readonly ResidencyBudgetLedger _residency;
    private long _residentBytes;
    private long _admittedDecodedBytes;
    private long _sequence;
    private long _entryGeneration;
    private long _pinSequence;
    private int _pendingCount;
    private int _admissionWaiters;
    private bool _disposed;

    public ChunkRequestScheduler(
        Func<ulong, CancellationToken, ValueTask<ChunkLease>> loader,
        Func<ulong, CancellationToken, ValueTask<ChunkLoadEstimate>> estimator,
        long decodedBudgetBytes,
        int maxConcurrency = 4,
        int maxQueuedRequests = 4096,
        ChunkStreamingMetrics? metrics = null,
        ResidencyBudgetLedger? residency = null)
        : this(
            loader,
            estimator,
            decodedBudgetBytes,
            maxConcurrency,
            maxQueuedRequests,
            metrics,
            residency,
            loaderReportsStoredBytes: false)
    {
    }

    private ChunkRequestScheduler(
        Func<ulong, CancellationToken, ValueTask<ChunkLease>> loader,
        Func<ulong, CancellationToken, ValueTask<ChunkLoadEstimate>> estimator,
        long decodedBudgetBytes,
        int maxConcurrency,
        int maxQueuedRequests,
        ChunkStreamingMetrics? metrics,
        ResidencyBudgetLedger? residency,
        bool loaderReportsStoredBytes,
        Func<ulong, ResidencyReservation, CancellationToken, ValueTask<ChunkLease>>? pageLoader = null,
        Func<ValueTask>? disposeLoader = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodedBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedRequests);
        _loader = loader;
        _pageLoader = pageLoader;
        _disposeLoader = disposeLoader;
        _estimator = estimator;
        _loaderReportsStoredBytes = loaderReportsStoredBytes;
        _residency = residency ?? new ResidencyBudgetLedger(new ResidencyBudgets
        {
            DecodedCpuBytes = decodedBudgetBytes,
        });
        _decodedBudget = Math.Min(
            decodedBudgetBytes,
            _residency.Budget(ResidencyClass.DecodedCpu));
        _maxQueuedRequests = maxQueuedRequests;
        Metrics = metrics ?? new ChunkStreamingMetrics();
        _residency.AvailabilityChanged += OnResidencyChanged;
        _residency.ReclaimRequested += ReclaimDecodedContent;
        _workers = Enumerable.Range(0, maxConcurrency)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public ChunkStreamingMetrics Metrics { get; }

    /// <summary>
    /// Shared four-class residency ledger. Decode cache entries reserve <see cref="ResidencyClass.DecodedCpu"/>
    /// here; upload and renderer consumers must reserve staging/GPU bytes from the same ledger.
    /// </summary>
    public ResidencyBudgetLedger Residency => _residency;

    public static ChunkRequestScheduler CreateForDocument(
        BinaryDocument document,
        long decodedBudgetBytes,
        int maxConcurrency = 4,
        int maxQueuedRequests = 4096,
        ChunkStreamingMetrics? metrics = null,
        ResidencyBudgetLedger? residency = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        ResidencyBudgetLedger sharedResidency = residency ?? new ResidencyBudgetLedger(
            new ResidencyBudgets
            {
                DecodedCpuBytes = decodedBudgetBytes,
            });
        ChunkStreamingMetrics sharedMetrics = metrics ?? new ChunkStreamingMetrics();
        var loader = new DocumentChunkLoader(document, sharedMetrics);

        return new ChunkRequestScheduler(
            loader.LoadAsync,
            loader.EstimateAsync,
            decodedBudgetBytes,
            maxConcurrency,
            maxQueuedRequests,
            sharedMetrics,
            sharedResidency,
            loaderReportsStoredBytes: true);
    }

    /// <summary>
    /// Uses the same queue, deduplication and eviction with final decoded pages. Capacity is
    /// reserved once, then transferred to those pages so retained users outlive cache disposal.
    /// The document is retained for queued work until this scheduler finishes shutting down.
    /// </summary>
    public static ChunkRequestScheduler CreateForDocument(BinaryDocument document, PageArena pages,
        long decodedBudgetBytes, int maxConcurrency = 4, int maxQueuedRequests = 4096,
        ChunkStreamingMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ChunkStreamingMetrics sharedMetrics = metrics ?? new();
        var loader = new DocumentChunkLoader(document, sharedMetrics, pages);
        try
        {
            return new ChunkRequestScheduler(loader.LoadAsync, loader.EstimateAsync, decodedBudgetBytes,
                maxConcurrency, maxQueuedRequests, sharedMetrics, pages.Residency,
                loaderReportsStoredBytes: true, loader.LoadPagesAsync, loader.DisposeAsync);
        }
        catch { loader.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public long ResidentBytes
    {
        get
        {
            lock (_gate)
                return _residentBytes;
        }
    }

    public int ResidentCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(static entry => entry.SourceLease is not null);
        }
    }

    /// <summary>
    /// Synchronous zero-allocation resident fast path. It never queues I/O; a successful call pins
    /// the cache entry and the returned ownership struct must be disposed exactly once.
    /// </summary>
    public bool TryAcquireResident(ulong key, out ResidentChunkLease lease)
    {
        if (key == 0)
            throw new ArgumentOutOfRangeException(nameof(key));
        Metrics.Request();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out CacheEntry? entry) && entry.SourceLease is not null)
            {
                Metrics.CacheHit();
                long pinToken = PinLocked(entry);
                lease = new ResidentChunkLease(this, key, entry.Generation, pinToken);
                return true;
            }

            Metrics.CacheMiss();
            lease = default;
            return false;
        }
    }

    public ValueTask<ResidentChunkLease> AcquireAsync(
        ulong key,
        ChunkRequestOptions options = default,
        CancellationToken cancellationToken = default)
    {
        if (key == 0)
            throw new ArgumentOutOfRangeException(nameof(key));
        Metrics.Request();

        CacheEntry entry;
        Task completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out entry!))
            {
                if (entry.SourceLease is not null)
                {
                    Metrics.CacheHit();
                    long pinToken = PinLocked(entry);
                    return ValueTask.FromResult(
                        new ResidentChunkLease(this, key, entry.Generation, pinToken));
                }

                entry.Waiters++;
                Metrics.Deduplicated();
                if (!entry.Loading && options.Priority > entry.Priority)
                    EnqueueLocked(entry, options.Priority);
                completion = entry.Completion.Task;
            }
            else
            {
                if (_pendingCount >= _maxQueuedRequests)
                    throw new InvalidOperationException($"Chunk request queue limit {_maxQueuedRequests} was reached.");
                Metrics.CacheMiss();
                entry = new CacheEntry(key, checked(++_entryGeneration))
                {
                    Waiters = 1,
                    Priority = options.Priority,
                };
                _entries.Add(key, entry);
                _pendingCount++;
                EnqueueLocked(entry, options.Priority);
                completion = entry.Completion.Task;
            }
        }

        return AwaitPendingAsync(key, options, cancellationToken, entry, completion);
    }

    private async ValueTask<ResidentChunkLease> AwaitPendingAsync(
        ulong key,
        ChunkRequestOptions options,
        CancellationToken cancellationToken,
        CacheEntry entry,
        Task completion)
    {
        try
        {
            await WaitForWaiterAsync(completion, options.Deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Metrics.Canceled();
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
                    entry.Waiters--;
            }
            _residency.NotifyAvailabilityChanged();
            throw;
        }
        catch (TimeoutException)
        {
            Metrics.Canceled();
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
                    entry.Waiters--;
            }
            _residency.NotifyAvailabilityChanged();
            throw;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out CacheEntry? current) && ReferenceEquals(current, entry))
                    entry.Waiters--;
            }
            _residency.NotifyAvailabilityChanged();
            throw;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(key, out CacheEntry? current)
                || !ReferenceEquals(current, entry)
                || entry.SourceLease is null)
            {
                throw new InvalidOperationException($"Chunk 0x{key:X16} completed without a resident cache entry.");
            }

            entry.Waiters--;
            long pinToken = PinLocked(entry);
            return new ResidentChunkLease(this, key, entry.Generation, pinToken);
        }
    }

    public int Trim()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            int evicted = 0;
            while (TryEvictOneLocked())
                evicted++;
            return evicted;
        }
    }

    internal ReadOnlyMemory<byte> GetPinnedMemory(ulong key, long generation, long pinToken)
    {
        lock (_gate) return PinnedChunk(key, generation, pinToken).Memory;
    }
    internal PageBuffer DuplicatePinnedPages(ulong key, long generation, long pinToken)
    {
        lock (_gate) return PinnedChunk(key, generation, pinToken).DuplicatePages();
    }

    internal PageRange<byte> RetainPinnedPages(ulong key, long generation, long pinToken, long offset, long? length)
    {
        lock (_gate)
        {
            ChunkLease chunk = PinnedChunk(key, generation, pinToken);
            return chunk.RetainPages(offset, length ?? chunk.Length - offset);
        }
    }

    private ChunkLease PinnedChunk(ulong key, long generation, long pinToken)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(key, out CacheEntry? entry) || entry.Generation != generation
            || entry.SourceLease is null || !entry.ActivePins.Contains(pinToken))
            throw new ObjectDisposedException(nameof(ResidentChunkLease), $"Resident chunk 0x{key:X16} is no longer valid.");
        return entry.SourceLease;
    }

    internal void Release(ulong key, long generation, long pinToken)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!_entries.TryGetValue(key, out CacheEntry? entry) || entry.Generation != generation)
                return;
            // ResidentChunkLease is an allocation-free ownership struct and can be copied by the
            // language. Token removal makes disposal idempotent across all accidental copies: one
            // logical acquisition can release at most one scheduler pin.
            if (!entry.ActivePins.Remove(pinToken))
                return;
            Metrics.Unpin(entry.Size);
            TouchLocked(entry);
        }
        _residency.NotifyAvailabilityChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (CacheEntry entry in _entries.Values)
                entry.Completion.TrySetCanceled(_shutdown.Token);
        }

        _shutdown.Cancel();
        _residency.AvailabilityChanged -= OnResidencyChanged;
        _residency.ReclaimRequested -= ReclaimDecodedContent;
        _queueSignal.Release(_workers.Length);
        SignalAdmission(force: true);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            long pinnedBytes = 0;
            foreach (CacheEntry entry in _entries.Values)
            {
                pinnedBytes = checked(pinnedBytes + (long)entry.ActivePins.Count * entry.Size);
                entry.SourceLease?.Dispose();
                entry.DecodedReservation?.Dispose();
                entry.DecodedReservation = null;
            }
            Metrics.ResidentRemoved(_residentBytes, pinnedBytes);
            _entries.Clear();
            _lru.Clear();
            _residentBytes = 0;
        }
        _queueSignal.Dispose();
        if (_disposeLoader is not null) await _disposeLoader().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            try
            {
                await _queueSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            QueuedLoad queued;
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (!_queue.TryDequeue(out queued!, out _))
                    continue;
                if (queued.Version != queued.Entry.QueueVersion
                    || queued.Entry.Loading
                    || queued.Entry.SourceLease is not null)
                {
                    continue;
                }
                queued.Entry.Loading = true;
                _pendingCount--;
            }

            await ExecuteLoadAsync(queued).ConfigureAwait(false);
        }
    }

    private async Task ExecuteLoadAsync(QueuedLoad queued)
    {
        CacheEntry entry = queued.Entry;
        ResidencyReservation? compressedReservation = null;
        ResidencyReservation? decodedReservation = null;
        ChunkLease? sourceLease = null;
        long admittedDecodedBytes = 0;
        try
        {
            ChunkLoadEstimate estimate = await _estimator(entry.Key, _shutdown.Token).ConfigureAwait(false);
            ValidateEstimate(entry.Key, estimate);
            long decodedCapacity = estimate.BufferBytes ?? estimate.DecodedBytes;

            while (decodedReservation is null)
            {
                Interlocked.Increment(ref _admissionWaiters);
                try
                {
                    Task budgetChanged;
                    lock (_budgetSignalGate)
                        budgetChanged = _budgetChanged.Task;
                    lock (_gate)
                    {
                        ThrowIfDisposed();
                        while ((decodedCapacity
                                > _decodedBudget - _residentBytes - _admittedDecodedBytes
                                || decodedCapacity > _residency.Available(ResidencyClass.DecodedCpu))
                               && TryEvictOneLocked(entry))
                        {
                        }

                        if (decodedCapacity
                                <= _decodedBudget - _residentBytes - _admittedDecodedBytes
                            && _residency.TryReservePair(
                                ResidencyClass.Compressed,
                                estimate.StoredBytes,
                                ResidencyClass.DecodedCpu,
                                decodedCapacity,
                                out compressedReservation,
                                out decodedReservation))
                        {
                            _admittedDecodedBytes = checked(
                                _admittedDecodedBytes + decodedCapacity);
                            admittedDecodedBytes = decodedCapacity;
                        }
                    }

                    if (decodedReservation is null)
                    {
                        _residency.RequestReclaim(ResidencyClass.DecodedCpu, decodedCapacity);
                        await budgetChanged.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _admissionWaiters);
                }
            }

            long loadStart = Stopwatch.GetTimestamp();
            sourceLease = _pageLoader is null
                ? await _loader(entry.Key, _shutdown.Token).ConfigureAwait(false)
                : await _pageLoader(entry.Key, decodedReservation, _shutdown.Token).ConfigureAwait(false);
            if (!_loaderReportsStoredBytes)
                Metrics.StoredBytesRead(estimate.StoredBytes);
            long loadEnd = Stopwatch.GetTimestamp();
            int size;
            try
            {
                size = checked((int)sourceLease.Length);
            }
            catch
            {
                sourceLease.Dispose();
                sourceLease = null;
                throw;
            }
            if (size != estimate.DecodedBytes)
            {
                throw new InvalidDataException(
                    $"Chunk 0x{entry.Key:X16} loader returned {size} decoded bytes after declaring " +
                    $"{estimate.DecodedBytes} bytes for pre-admission.");
            }

            compressedReservation!.Dispose();
            compressedReservation = null;

            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(entry.Key, out CacheEntry? current)
                    || !ReferenceEquals(current, entry))
                {
                    throw new OperationCanceledException("Chunk scheduler stopped before publication.", _shutdown.Token);
                }

                entry.SourceLease = sourceLease;
                sourceLease = null;
                entry.DecodedReservation = decodedReservation;
                decodedReservation = null;
                entry.Size = decodedCapacity;
                _residentBytes += decodedCapacity;
                _admittedDecodedBytes -= admittedDecodedBytes;
                admittedDecodedBytes = 0;
                TouchLocked(entry);
                entry.Completion.TrySetResult();
                Metrics.ResidentAdded(decodedCapacity);
                Metrics.LoadCompleted(
                    size,
                    loadStart - queued.EnqueuedTimestamp,
                    loadEnd - loadStart);
            }
            _residency.NotifyAvailabilityChanged();
        }
        catch (Exception exception)
        {
            sourceLease?.Dispose();
            compressedReservation?.Dispose();
            decodedReservation?.Dispose();
            lock (_gate)
            {
                if (admittedDecodedBytes != 0)
                {
                    _admittedDecodedBytes -= admittedDecodedBytes;
                    admittedDecodedBytes = 0;
                }
                if (_entries.TryGetValue(entry.Key, out CacheEntry? current) && ReferenceEquals(current, entry))
                    _entries.Remove(entry.Key);
                entry.Completion.TrySetException(exception);
                Metrics.LoadFailed();
            }
            _residency.NotifyAvailabilityChanged();
        }
    }

    private void EnqueueLocked(CacheEntry entry, int priority)
    {
        entry.Priority = priority;
        long version = checked(++entry.QueueVersion);
        long sequence = checked(++_sequence);
        _queue.Enqueue(
            new QueuedLoad(entry, version, Stopwatch.GetTimestamp()),
            (-(long)priority, sequence));
        _queueSignal.Release();
    }

    private static Task WaitForWaiterAsync(
        Task completion,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        if (deadline is not DateTimeOffset deadlineAt)
            return completion.WaitAsync(cancellationToken);

        TimeSpan remaining = deadlineAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return Task.FromException(new TimeoutException("Chunk request deadline elapsed before completion."));
        return completion.WaitAsync(remaining, cancellationToken);
    }

    private void ValidateEstimate(ulong key, ChunkLoadEstimate estimate)
    {
        if (estimate.StoredBytes < 0 || estimate.DecodedBytes < 0)
            throw new InvalidDataException($"Chunk 0x{key:X16} declared a negative load estimate.");
        if (estimate.DecodedBytes > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Chunk 0x{key:X16} declares {estimate.DecodedBytes} decoded bytes and must be semantically subdivided.");
        }
        if (estimate.StoredBytes > _residency.Budget(ResidencyClass.Compressed))
        {
            throw new InvalidOperationException(
                $"Chunk 0x{key:X16} stored payload ({estimate.StoredBytes} bytes) exceeds compressed budget " +
                $"{_residency.Budget(ResidencyClass.Compressed)} before I/O admission.");
        }
        if (estimate.BufferBytes is { } capacity && capacity < estimate.DecodedBytes)
            throw new InvalidDataException("The declared buffer capacity is smaller than its decoded content.");
        if ((estimate.BufferBytes ?? estimate.DecodedBytes) > _decodedBudget)
        {
            throw new InvalidOperationException(
                $"Chunk 0x{key:X16} decoded payload ({estimate.DecodedBytes} bytes) exceeds decoded budget " +
                $"{_decodedBudget} before I/O admission.");
        }
    }

    private void OnResidencyChanged() => SignalAdmission();

    private void ReclaimDecodedContent(ResidencyClass residencyClass, long requiredBytes)
    {
        if (residencyClass != ResidencyClass.DecodedCpu) return;
        lock (_gate)
        {
            if (_disposed) return;
            while (_residency.Available(residencyClass) < requiredBytes && TryEvictOneLocked())
            {
            }
        }
    }

    private void SignalAdmission(bool force = false)
    {
        if (!force && Volatile.Read(ref _admissionWaiters) == 0)
            return;
        TaskCompletionSource changed;
        lock (_budgetSignalGate)
        {
            changed = _budgetChanged;
            _budgetChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    private long PinLocked(CacheEntry entry)
    {
        long pinToken = checked(++_pinSequence);
        if (!entry.ActivePins.Add(pinToken))
            throw new InvalidOperationException("Resident pin token collision.");
        Metrics.Pin(entry.Size);
        TouchLocked(entry);
        return pinToken;
    }

    private void TouchLocked(CacheEntry entry)
    {
        if (entry.LruNode is not null)
        {
            _lru.Remove(entry.LruNode);
            _lru.AddLast(entry.LruNode);
            return;
        }
        entry.LruNode = _lru.AddLast(entry);
    }

    private bool TryEvictOneLocked(CacheEntry? protectedEntry = null)
    {
        LinkedListNode<CacheEntry>? node = _lru.First;
        while (node is not null)
        {
            CacheEntry entry = node.Value;
            node = node.Next;
            if (ReferenceEquals(entry, protectedEntry)
                || entry.SourceLease is null
                || entry.ActivePins.Count != 0
                || entry.Waiters != 0)
            {
                continue;
            }

            _lru.Remove(entry.LruNode!);
            entry.LruNode = null;
            _entries.Remove(entry.Key);
            entry.SourceLease.Dispose();
            entry.SourceLease = null;
            entry.DecodedReservation?.Dispose();
            entry.DecodedReservation = null;
            _residentBytes -= entry.Size;
            Metrics.Evicted(entry.Size);
            return true;
        }

        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class CacheEntry
    {
        internal CacheEntry(ulong key, long generation)
        {
            Key = key;
            Generation = generation;
        }

        internal ulong Key { get; }
        internal long Generation { get; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ChunkLease? SourceLease { get; set; }
        internal ResidencyReservation? DecodedReservation { get; set; }
        internal LinkedListNode<CacheEntry>? LruNode { get; set; }
        internal long Size { get; set; }
        internal int Waiters { get; set; }
        internal HashSet<long> ActivePins { get; } = [];
        internal int Priority { get; set; }
        internal long QueueVersion { get; set; }
        internal bool Loading { get; set; }
    }

    private sealed record QueuedLoad(
        CacheEntry Entry,
        long Version,
        long EnqueuedTimestamp);
}
