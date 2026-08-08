using System.Diagnostics.CodeAnalysis;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Frame;

/// <summary>
/// Coordinates read access to persistent render resources without owning those resources.
/// A prepare scope is the exclusive writer; one renderer frame and any number of observations
/// may read concurrently between prepare scopes.
/// </summary>
public sealed class RenderFrameCoordinator : IDisposable
{
    private const int LifecycleActive = 0;
    private const int LifecycleClosing = 1;
    private const int LifecycleClosed = 2;
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(30);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly TimeSpan _disposeTimeout;
    private QueueCompletion[] _readerFences = [];
    private QueueCompletion[] _shutdownFences = [];
    private readonly HashSet<RenderTimeline> _frameTimelines = [];
    private readonly Dictionary<RenderTimeline, int> _frameTimelineGenerationMasks = [];
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private RenderPrepareScope? _openPrepare;
    private int _openReaderCount;
    private int _openObservationCount;
    private int _pendingTimelineCount;
    private int _retryRequiredTimelineCount;
    private bool _frameOpen;
    private int _lifecycleState;

    public RenderFrameCoordinator(
        IGraphicsBackend backend,
        Device device,
        TimeSpan? disposeTimeout = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _disposeTimeout = disposeTimeout ?? DefaultDisposeTimeout;
        if (_disposeTimeout <= TimeSpan.Zero || _disposeTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposeTimeout),
                "Render-frame coordination requires a finite positive disposal timeout.");
        }
    }

    /// <summary>
    /// Creates an opaque timeline for one pipeline or persistent resource owner. A timeline is
    /// permanently bound to this coordinator instance, even when another coordinator uses the
    /// same graphics device.
    /// </summary>
    internal RenderTimeline CreateTimeline(int generationCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generationCount);
        if (generationCount > sizeof(int) * 8)
            throw new ArgumentOutOfRangeException(nameof(generationCount));
        ThrowIfNotActive();
        lock (_gate)
        {
            ThrowIfNotActive();
            return new RenderTimeline(this, _backend, _device, generationCount);
        }
    }

    /// <summary>
    /// Begins the unique renderer frame. Outstanding observations and already-submitted readers
    /// do not conflict with another read, but an open frame, an exclusive prepare, or unconsumed
    /// work on any registered timeline does.
    /// </summary>
    public bool TryBeginFrame([NotNullWhen(true)] out RenderFrame? frame)
    {
        ThrowIfNotActive();
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_openPrepare is not null ||
                _frameOpen ||
                _pendingTimelineCount != 0 ||
                _retryRequiredTimelineCount != 0)
            {
                frame = null;
                return false;
            }

            _frameOpen = true;
            _openReaderCount = checked(_openReaderCount + 1);
            frame = new RenderFrame(new RenderReadOwnership(this, RenderReadKind.Frame));
            return true;
        }
    }

    /// <summary>
    /// Begins an observation-only read for diagnostics, capture, or readback. Observations protect
    /// resource lifetime but never publish pipeline timeline submissions.
    /// </summary>
    public bool TryBeginObservation([NotNullWhen(true)] out RenderObservation? observation)
    {
        ThrowIfNotActive();
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_openPrepare is not null)
            {
                observation = null;
                return false;
            }

            _openReaderCount = checked(_openReaderCount + 1);
            _openObservationCount = checked(_openObservationCount + 1);
            observation = new RenderObservation(
                new RenderReadOwnership(this, RenderReadKind.Observation));
            return true;
        }
    }

    /// <summary>
    /// Acquires exclusive permission for render systems to update their own persistent resources.
    /// Each system claims only its own timeline from the returned scope.
    /// </summary>
    public bool TryBeginPrepare([NotNullWhen(true)] out RenderPrepareScope? scope)
    {
        ThrowIfNotActive();
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_openPrepare is not null || _openReaderCount != 0 || !ReleaseCompletedReadersLocked())
            {
                scope = null;
                return false;
            }

            scope = new RenderPrepareScope(this);
            _openPrepare = scope;
            return true;
        }
    }

    public RenderFrameSynchronizationDiagnostics CaptureDiagnostics()
    {
        lock (_gate)
        {
            int lifecycle = Volatile.Read(ref _lifecycleState);
            return new RenderFrameSynchronizationDiagnostics(
                _openPrepare is not null,
                _frameOpen,
                _openReaderCount,
                _openObservationCount,
                CountReaderPositionsLocked(),
                _pendingTimelineCount,
                _retryRequiredTimelineCount,
                lifecycle == LifecycleClosing,
                lifecycle == LifecycleClosed);
        }
    }

    internal void AdmitFrameResources()
    {
        ThrowIfNotActive();
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_openPrepare is not null || _frameOpen || _openReaderCount != 0)
            {
                throw new InvalidOperationException(
                    "Frame resources can only be admitted between render ownership scopes.");
            }
            // Automatic RHI retirement is advanced by Queue submission/completion. This boundary
            // remains the renderer's ownership admission point and requires no Device-side arena.
        }
    }

    /// <summary>
    /// Waits for every submitted frame or observation tracked by this coordinator without closing
    /// it. This is a shutdown boundary, not a frame admission path: callers may subsequently open
    /// the final prepare scope that destroys persistent render resources.
    /// </summary>
    public void WaitForTrackedSubmissions()
    {
        ThrowIfNotActive();
        long started = Environment.TickCount64;
        lock (_gate)
        {
            ThrowIfNotActive();
            if (_openReaderCount != 0 || _openPrepare is not null)
            {
                throw new InvalidOperationException(
                    "Tracked render submissions can only be drained without an open read or prepare scope.");
            }

            foreach (QueueCompletion fence in _shutdownFences)
            {
                if (_backend.WaitCpu(fence, Remaining(started)) != WaitStatus.Completed)
                {
                    throw new TimeoutException(
                        $"Render submission {fence.Queue}:{fence.Value} did not complete before shutdown.");
                }
            }
            _readerFences = [];
            _shutdownFences = [];
        }
    }

    internal void RegisterFrameTimelines(ReadOnlySpan<RenderTimeline> timelines)
    {
        if (timelines.IsEmpty)
            throw new ArgumentException("At least one render timeline is required.", nameof(timelines));

        lock (_gate)
        {
            ThrowIfClosed();
            if (!_frameOpen)
                throw new InvalidOperationException("Render-frame ownership is already closed.");

            // Validate the complete set before publishing any registration so a foreign timeline
            // cannot leave an otherwise failed frame use partially registered.
            for (int i = 0; i < timelines.Length; i++)
            {
                RenderTimeline timeline = timelines[i]
                    ?? throw new ArgumentException("A render timeline cannot be null.", nameof(timelines));
                ValidateTimelineOwner(timeline);
            }

            for (int i = 0; i < timelines.Length; i++)
                _frameTimelines.Add(timelines[i]);
        }
    }

    internal void RegisterFrameTimelineGeneration(
        RenderTimeline timeline,
        int generation)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        lock (_gate)
        {
            ThrowIfClosed();
            if (!_frameOpen)
                throw new InvalidOperationException("Render-frame ownership is already closed.");
            ValidateTimelineOwner(timeline);
            if (!_frameTimelines.Contains(timeline))
            {
                throw new InvalidOperationException(
                    "A frame-use lease must register its timeline before a physical generation.");
            }
            if ((uint)generation >= (uint)timeline.GenerationCount)
                throw new ArgumentOutOfRangeException(nameof(generation));
            int bit = 1 << generation;
            _frameTimelineGenerationMasks.TryGetValue(timeline, out int mask);
            _frameTimelineGenerationMasks[timeline] = mask | bit;
        }
    }

    internal ulong ClaimTimeline(RenderPrepareScope scope, RenderTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(timeline);
        lock (_gate)
        {
            ThrowIfClosed();
            if (!ReferenceEquals(_openPrepare, scope))
                throw new InvalidOperationException("Render prepare ownership is already closed.");
            ValidateTimelineOwner(timeline);
            return timeline.PendingSubmissionSequence;
        }
    }

    internal void CompleteRead(
        RenderReadKind kind,
        ReadOnlySpan<QueueCompletion> completions)
    {
        if (completions.IsEmpty)
        {
            throw new ArgumentException(
                "An empty fence list cannot close submitted render work; dispose an unsubmitted read instead.",
                nameof(completions));
        }
        QueueCompletion[] fences = RenderQueueCompletions.Merge(completions, default);

        lock (_gate)
        {
            ThrowIfClosed();
            ValidateOpenReadLocked(kind);

            foreach (QueueCompletion fence in fences)
            {
                if (!ReferenceEquals(fence.Queue.Device, _device))
                {
                    throw new ArgumentException(
                        "A render completion belongs to another graphics Device.",
                        nameof(completions));
                }
            }

            if (kind == RenderReadKind.Frame)
            {
                foreach (RenderTimeline timeline in _frameTimelines)
                {
                    if (timeline.LastSubmissionSequence == ulong.MaxValue)
                    {
                        throw new OverflowException(
                            "The render timeline exhausted its submission sequence space.");
                    }
                }
            }

            _shutdownFences = RenderQueueCompletions.Merge(_shutdownFences, fences);
            if (kind == RenderReadKind.Observation
                || HasUnversionedFrameTimelineLocked())
            {
                _readerFences = RenderQueueCompletions.Merge(_readerFences, fences);
            }

            if (kind == RenderReadKind.Frame)
            {
                PublishFrameGenerationRetirementsLocked(fences);
                PublishFrameTimelinesLocked();
            }
            CloseReadLocked(kind);
        }
    }

    internal void AbandonRead(RenderReadKind kind)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            ValidateOpenReadLocked(kind);
            CloseReadLocked(kind);
        }
    }

    internal void CommitPrepare(
        RenderPrepareScope scope,
        ReadOnlySpan<RenderTimelineClaim> claims)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (!ReferenceEquals(_openPrepare, scope))
                throw new InvalidOperationException("Render prepare ownership is already closed.");

            foreach (RenderTimelineClaim claim in claims)
            {
                ValidateTimelineOwner(claim.Timeline);
                if (claim.PendingSubmissionSequence != claim.Timeline.PendingSubmissionSequence)
                {
                    throw new InvalidOperationException(
                        "The claimed timeline submission changed before prepare commit.");
                }
            }

            foreach (RenderTimelineClaim claim in claims)
            {
                RenderTimeline timeline = claim.Timeline;
                if (timeline.PendingSubmissionSequence != 0)
                {
                    timeline.PendingSubmissionSequence = 0;
                    _pendingTimelineCount--;
                }
                if (timeline.RetryRequired)
                {
                    timeline.RetryRequired = false;
                    _retryRequiredTimelineCount--;
                }
            }

            _openPrepare = null;
            Monitor.PulseAll(_gate);
        }
    }

    internal void AbandonPrepare(
        RenderPrepareScope scope,
        ReadOnlySpan<RenderTimelineClaim> claims)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (!ReferenceEquals(_openPrepare, scope))
                throw new InvalidOperationException("Render prepare ownership is already closed.");

            foreach (RenderTimelineClaim claim in claims)
            {
                ValidateTimelineOwner(claim.Timeline);
                if (!claim.Timeline.RetryRequired)
                {
                    claim.Timeline.RetryRequired = true;
                    _retryRequiredTimelineCount++;
                }
            }

            _openPrepare = null;
            Monitor.PulseAll(_gate);
        }
    }

    private void ValidateTimelineOwner(RenderTimeline timeline)
    {
        if (!ReferenceEquals(timeline.Owner, this))
        {
            throw new ArgumentException(
                "The render timeline belongs to another coordinator instance.",
                nameof(timeline));
        }
    }

    private void PublishFrameTimelinesLocked()
    {
        foreach (RenderTimeline timeline in _frameTimelines)
        {
            // TryBeginFrame prevents a second publication while any timeline is pending.
            ulong sequence = timeline.LastSubmissionSequence + 1;
            timeline.LastSubmissionSequence = sequence;
            timeline.PendingSubmissionSequence = sequence;
            _pendingTimelineCount++;
        }
    }

    private void PublishFrameGenerationRetirementsLocked(
        ReadOnlySpan<QueueCompletion> fences)
    {
        foreach ((RenderTimeline timeline, int mask) in _frameTimelineGenerationMasks)
        {
            for (int generation = 0; generation < timeline.GenerationCount; generation++)
            {
                if ((mask & (1 << generation)) != 0)
                    timeline.RegisterSubmission(generation, fences);
            }
        }
    }

    private bool HasUnversionedFrameTimelineLocked()
    {
        foreach (RenderTimeline timeline in _frameTimelines)
        {
            if (timeline.GenerationCount == 0)
                return true;
        }
        return false;
    }

    private void ValidateOpenReadLocked(RenderReadKind kind)
    {
        if (_openReaderCount <= 0)
            throw new InvalidOperationException("Render reader ownership is already closed.");
        if (kind == RenderReadKind.Frame && !_frameOpen)
            throw new InvalidOperationException("Render-frame ownership is already closed.");
        if (kind == RenderReadKind.Observation && _openObservationCount <= 0)
            throw new InvalidOperationException("Render-observation ownership is already closed.");
    }

    private void CloseReadLocked(RenderReadKind kind)
    {
        _openReaderCount--;
        if (kind == RenderReadKind.Frame)
        {
            _frameOpen = false;
            _frameTimelines.Clear();
            _frameTimelineGenerationMasks.Clear();
        }
        else
        {
            _openObservationCount--;
        }
        Monitor.PulseAll(_gate);
    }

    private bool ReleaseCompletedReadersLocked()
    {
        QueueCompletion[] pending = new QueueCompletion[3];
        int pendingCount = 0;
        foreach (QueueCompletion fence in _readerFences)
        {
            if (!_backend.IsComplete(fence))
                pending[pendingCount++] = fence;
        }
        _readerFences = pending.AsSpan(0, pendingCount).ToArray();
        return pendingCount == 0;
    }

    private int CountReaderPositionsLocked()
    {
        return _readerFences.Length;
    }

    private void ThrowIfNotActive()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _lifecycleState) != LifecycleActive,
            this);

    private void ThrowIfClosed()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _lifecycleState) == LifecycleClosed,
            this);

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (Volatile.Read(ref _lifecycleState) == LifecycleClosed)
                return;
            if (Volatile.Read(ref _lifecycleState) == LifecycleActive)
                Volatile.Write(ref _lifecycleState, LifecycleClosing);

            long started = Environment.TickCount64;
            lock (_gate)
            {
                while (_openReaderCount != 0 || _openPrepare is not null)
                {
                    TimeSpan remaining = Remaining(started);
                    if (remaining == TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                    {
                        throw new TimeoutException(
                            $"Render-frame shutdown still owns {_openReaderCount} read(s)" +
                            (_openPrepare is not null ? " and one prepare scope." : "."));
                    }
                }

                foreach (QueueCompletion fence in _readerFences)
                {
                    if (_backend.WaitCpu(fence, Remaining(started)) != WaitStatus.Completed)
                    {
                        throw new TimeoutException(
                            $"Render reader {fence.Queue}:{fence.Value} did not complete before shutdown.");
                    }
                }
                _readerFences = [];

                foreach (QueueCompletion fence in _shutdownFences)
                {
                    if (_backend.WaitCpu(fence, Remaining(started)) != WaitStatus.Completed)
                    {
                        throw new TimeoutException(
                            $"Render submission {fence.Queue}:{fence.Value} did not complete before shutdown.");
                    }
                }
                _shutdownFences = [];

                Volatile.Write(ref _lifecycleState, LifecycleClosed);
                Monitor.PulseAll(_gate);
            }
        }
    }

    private TimeSpan Remaining(long started)
    {
        TimeSpan remaining = _disposeTimeout -
            TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
