using SomeEngine.Graphics;

namespace SomeEngine.Render.Frame;

/// <summary>
/// Owns the unique read for one renderer frame. Pipelines explicitly register only the timelines
/// whose persistent state is consumed by submitted work.
/// </summary>
public sealed class RenderFrame : IDisposable
{
    private readonly RenderReadOwnership _ownership;

    internal RenderFrame(RenderReadOwnership ownership)
        => _ownership = ownership;

    public bool IsClosed => _ownership.IsClosed;

    /// <summary>
    /// Atomically registers every timeline consumed by one command-recording use. The returned
    /// lease must remain alive until recording has stopped using the associated resources.
    /// </summary>
    internal RenderFrameUseLease AcquireUse(ReadOnlySpan<RenderTimeline> timelines)
        => _ownership.AcquireUse(timelines);

    /// <summary>
    /// Closes the submitted frame with the exact queue completions that accepted its work.
    /// Registered timelines receive their next submission sequence only after validation succeeds.
    /// </summary>
    public void Complete(ReadOnlySpan<QueueCompletion> completions)
        => _ownership.Complete(completions);

    public void Dispose()
        => _ownership.Dispose();
}

/// <summary>
/// Owns an observation-only read used by diagnostics, capture, or readback. It protects the same
/// prepare boundary as a frame but cannot publish pipeline timelines.
/// </summary>
public sealed class RenderObservation : IDisposable
{
    private readonly RenderReadOwnership _ownership;

    internal RenderObservation(RenderReadOwnership ownership)
        => _ownership = ownership;

    public bool IsClosed => _ownership.IsClosed;

    public void Complete(ReadOnlySpan<QueueCompletion> completions)
        => _ownership.Complete(completions);

    public void Dispose()
        => _ownership.Dispose();
}

internal enum RenderReadKind : byte
{
    Frame,
    Observation,
}

internal sealed class RenderReadOwnership : IDisposable
{
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateClosed = 2;

    private readonly object _gate = new();
    private RenderFrameCoordinator? _owner;
    private readonly RenderReadKind _kind;
    private int _activeUses;
    private int _state;

    internal RenderReadOwnership(RenderFrameCoordinator owner, RenderReadKind kind)
    {
        _owner = owner;
        _kind = kind;
    }

    internal bool IsClosed
    {
        get
        {
            lock (_gate)
                return _state == StateClosed;
        }
    }

    internal RenderFrameUseLease AcquireUse(ReadOnlySpan<RenderTimeline> timelines)
    {
        if (timelines.IsEmpty)
            throw new ArgumentException("A render-frame use requires at least one timeline.", nameof(timelines));

        lock (_gate)
        {
            ThrowIfNotOpen();
            if (_kind != RenderReadKind.Frame)
                throw new InvalidOperationException("Observations cannot acquire render-frame uses.");

            RenderFrameCoordinator owner = _owner
                ?? throw new ObjectDisposedException(_kind.ToString());
            owner.RegisterFrameTimelines(timelines);
            _activeUses = checked(_activeUses + 1);
            return new RenderFrameUseLease(this);
        }
    }

    internal void Complete(ReadOnlySpan<QueueCompletion> completions)
    {
        lock (_gate)
        {
            ThrowIfNotOpen();
            ThrowIfUseActive();
            _state = StateClosing;
            try
            {
                RenderFrameCoordinator owner = _owner
                    ?? throw new ObjectDisposedException(_kind.ToString());
                owner.CompleteRead(_kind, completions);
                _owner = null;
                _state = StateClosed;
            }
            catch
            {
                _state = StateOpen;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == StateClosed)
                return;
            ThrowIfNotOpen();
            ThrowIfUseActive();
            _state = StateClosing;
            try
            {
                RenderFrameCoordinator? owner = _owner;
                owner?.AbandonRead(_kind);
                _owner = null;
                _state = StateClosed;
            }
            catch
            {
                _state = StateOpen;
                throw;
            }
        }
    }

    internal void ReleaseUse()
    {
        lock (_gate)
        {
            if (_state != StateOpen || _activeUses <= 0)
                throw new InvalidOperationException("The render-frame use is no longer active.");
            _activeUses--;
        }
    }

    internal void RegisterGeneration(RenderTimeline timeline, int generation)
    {
        lock (_gate)
        {
            if (_state != StateOpen || _activeUses <= 0)
            {
                throw new InvalidOperationException(
                    "A render-frame generation requires an active frame-use lease.");
            }
            RenderFrameCoordinator owner = _owner
                ?? throw new ObjectDisposedException(_kind.ToString());
            owner.RegisterFrameTimelineGeneration(timeline, generation);
        }
    }

    private void ThrowIfNotOpen()
    {
        if (_state != StateOpen || _owner is null)
            throw new ObjectDisposedException(_kind.ToString());
    }

    private void ThrowIfUseActive()
    {
        if (_activeUses != 0)
        {
            throw new InvalidOperationException(
                "All render-frame use leases must be released before closing the read.");
        }
    }
}

/// <summary>
/// Linear recording ownership for a set of timelines consumed by one frame use.
/// </summary>
internal sealed class RenderFrameUseLease : IDisposable
{
    private RenderReadOwnership? _owner;
    private int _closed;

    internal RenderFrameUseLease(RenderReadOwnership owner)
        => _owner = owner;

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal void RegisterGeneration(RenderTimeline timeline, int generation)
    {
        if (Volatile.Read(ref _closed) != 0)
            throw new ObjectDisposedException(nameof(RenderFrameUseLease));
        RenderReadOwnership owner = _owner
            ?? throw new ObjectDisposedException(nameof(RenderFrameUseLease));
        owner.RegisterGeneration(timeline, generation);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
            return;
        try
        {
            RenderReadOwnership owner = _owner
                ?? throw new ObjectDisposedException(nameof(RenderFrameUseLease));
            owner.ReleaseUse();
            _owner = null;
        }
        catch
        {
            Volatile.Write(ref _closed, 0);
            throw;
        }
    }
}
