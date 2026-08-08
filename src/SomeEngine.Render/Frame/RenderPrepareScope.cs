using SomeEngine.Core.Collections;

namespace SomeEngine.Render.Frame;

/// <summary>
/// Exclusive write ownership for one render prepare operation. Each pipeline acquires a linear
/// mutation lease for its timeline and commits only after every acquired lease has been released.
/// </summary>
public sealed class RenderPrepareScope : IDisposable
{
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateCommitted = 2;
    private const int StateAbandoned = 3;

    private SmallList<RenderTimelineClaim> _claims;
    private SmallList<RenderTimeline> _activeTimelines;
    private readonly object _gate = new();
    private RenderFrameCoordinator? _owner;
    private int _state;

    internal RenderPrepareScope(RenderFrameCoordinator owner)
        => _owner = owner;

    public bool IsClosed
    {
        get
        {
            lock (_gate)
                return _state >= StateCommitted;
        }
    }

    internal bool IsCommitted
    {
        get
        {
            lock (_gate)
                return _state == StateCommitted;
        }
    }

    internal bool IsAbandoned
    {
        get
        {
            lock (_gate)
                return _state == StateAbandoned;
        }
    }

    /// <summary>
    /// Claims exclusive mutation access for a timeline. The returned sequence identifies the
    /// latest submitted frame awaiting consumption by that timeline, or zero when no frame is
    /// pending. A timeline can have only one outstanding lease; after that lease is released the
    /// same scope may acquire it again without creating a second claim.
    /// </summary>
    internal RenderTimelineLease AcquireTimeline(RenderTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        lock (_gate)
        {
            ThrowIfNotOpen();
            int claimIndex = FindClaim(timeline);
            RenderTimelineClaim claim;
            if (claimIndex < 0)
            {
                RenderFrameCoordinator owner = _owner
                    ?? throw new ObjectDisposedException(nameof(RenderPrepareScope));
                ulong pendingSequence = owner.ClaimTimeline(this, timeline);
                claim = new RenderTimelineClaim(timeline, pendingSequence);
                _claims.Add(claim);
            }
            else
            {
                claim = _claims[claimIndex];
            }

            if (_activeTimelines.IndexOf(timeline) >= 0)
            {
                throw new InvalidOperationException(
                    "The render timeline already has an active mutation lease in this prepare scope.");
            }
            _activeTimelines.Add(timeline);
            return new RenderTimelineLease(this, claim);
        }
    }

    /// <summary>
    /// Commits every timeline actually claimed by this scope. Timelines skipped by the scope keep
    /// their pending submission or retry state. Active mutation leases make commit invalid and
    /// leave this scope open.
    /// </summary>
    public void Commit()
    {
        lock (_gate)
        {
            ThrowIfNotOpen();
            ThrowIfLeaseActive();
            _state = StateClosing;
            try
            {
                RenderFrameCoordinator owner = _owner
                    ?? throw new ObjectDisposedException(nameof(RenderPrepareScope));
                owner.CommitPrepare(this, _claims.AsSpan());
                _owner = null;
                _state = StateCommitted;
                ClearClaims();
            }
            catch
            {
                _state = StateOpen;
                throw;
            }
        }
    }

    /// <summary>
    /// Abandons this prepare. Every claimed timeline becomes retry-required, including a timeline
    /// claimed before its first submitted frame. Active leases make disposal invalid and leave the
    /// scope and coordinator ownership open.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_state >= StateCommitted)
                return;
            if (_state != StateOpen)
                throw new InvalidOperationException("Render prepare ownership is closing.");
            ThrowIfLeaseActive();
            _state = StateClosing;
            try
            {
                RenderFrameCoordinator owner = _owner
                    ?? throw new ObjectDisposedException(nameof(RenderPrepareScope));
                owner.AbandonPrepare(this, _claims.AsSpan());
                _owner = null;
                _state = StateAbandoned;
                ClearClaims();
            }
            catch
            {
                _state = StateOpen;
                throw;
            }
        }
    }

    internal void ReleaseLease(in RenderTimelineClaim claim)
    {
        lock (_gate)
        {
            int claimIndex = FindClaim(claim.Timeline);
            if (_state != StateOpen ||
                claimIndex < 0 ||
                _claims[claimIndex] != claim)
            {
                throw new InvalidOperationException("The render timeline lease is no longer owned by this scope.");
            }
            if (!_activeTimelines.RemoveStable(claim.Timeline))
                throw new InvalidOperationException("The render timeline lease was already released.");
        }
    }

    private int FindClaim(RenderTimeline timeline)
    {
        Span<RenderTimelineClaim> claims = _claims.AsSpan();
        for (int index = 0; index < claims.Length; index++)
        {
            if (ReferenceEquals(claims[index].Timeline, timeline))
                return index;
        }
        return -1;
    }

    private void ClearClaims()
    {
        _claims.Clear();
        _activeTimelines.Clear();
    }

    private void ThrowIfNotOpen()
    {
        if (_state != StateOpen || _owner is null)
            throw new ObjectDisposedException(nameof(RenderPrepareScope));
    }

    private void ThrowIfLeaseActive()
    {
        if (_activeTimelines.Count != 0)
        {
            throw new InvalidOperationException(
                "All render timeline mutation leases must be released before closing prepare.");
        }
    }
}
