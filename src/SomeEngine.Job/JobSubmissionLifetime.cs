namespace SomeEngine.Job;

using System.Buffers;

/// <summary>
/// Receives scheduler root-submission boundaries for a higher-level lifetime owner. Returning
/// <see langword="false"/> means the submission is already covered by an owned ancestor scope.
/// </summary>
internal interface IJobSubmissionObserver
{
    bool TryBeginSubmission(JobHandle currentScope);

    void CommitSubmission(JobHandle handle);

    void RollbackSubmission(JobHandle handle);
}

/// <summary>
/// An ambient, synchronous submission owner. The scope is deliberately thread-bound: it captures
/// roots launched by a lifecycle callback, while descendants launched by jobs remain covered by
/// the scheduler's ordinary parent/child scope-completion contract.
/// </summary>
internal struct JobSubmissionScope : IDisposable
{
    private JobSubmissionTracker.Frame? _frame;

    internal JobSubmissionScope(JobSubmissionTracker.Frame frame)
    {
        _frame = frame;
    }

    public void Dispose()
    {
        JobSubmissionTracker.Frame? frame = _frame;
        if (frame is null)
            return;

        JobSubmissionTracker.Exit(frame);
        _frame = null;
    }
}

/// <summary>
/// A close-once collection of root job scopes. Closing rejects unrelated new roots, waits every
/// admitted root through full scope completion, and still permits descendants of those roots to
/// be attached while closure is in progress.
/// </summary>
internal sealed class JobLifetime : IJobSubmissionObserver
{
    private const int Open = 0;
    private const int Closing = 1;
    private const int Closed = 2;

    private readonly object _gate = new();
    private readonly string _ownerName;
    private readonly List<JobHandle> _roots = [];
    private int _state;
    private int _pendingSubmissions;

    internal JobLifetime(string ownerName)
    {
        _ownerName = string.IsNullOrWhiteSpace(ownerName)
            ? throw new ArgumentException("A Job lifetime owner name is required.", nameof(ownerName))
            : ownerName;
    }

    internal bool IsOpen
    {
        get
        {
            lock (_gate)
                return _state == Open;
        }
    }

    internal int TrackedRootCount
    {
        get
        {
            lock (_gate)
            {
                RetireObservedRootsUnderGate();
                return _roots.Count;
            }
        }
    }

    internal JobSubmissionScope EnterSubmissionScope() =>
        JobSubmissionTracker.Enter(this);

    internal bool OwnsCurrentScope()
    {
        JobHandle currentScope = JobSystem.GetCurrentScope();
        if (currentScope.Index == 0)
            return false;

        lock (_gate)
            return IsOwnedScopeUnderGate(currentScope);
    }

    internal void ThrowIfClosed()
    {
        lock (_gate)
        {
            if (_state != Open)
                throw new ObjectDisposedException(_ownerName);
        }
    }

    bool IJobSubmissionObserver.TryBeginSubmission(JobHandle currentScope)
    {
        lock (_gate)
        {
            RetireObservedRootsUnderGate();
            if (currentScope.Index != 0 && IsOwnedScopeUnderGate(currentScope))
                return false;

            if (_state != Open)
                throw new ObjectDisposedException(_ownerName);

            _pendingSubmissions = checked(_pendingSubmissions + 1);
            return true;
        }
    }

    void IJobSubmissionObserver.CommitSubmission(JobHandle handle)
    {
        lock (_gate)
        {
            if (_pendingSubmissions <= 0)
                throw new InvalidOperationException("Job lifetime submission accounting is unbalanced.");

            _roots.Add(handle);
            JobSystem.OnCompleted(
                handle,
                static (completed, state) =>
                    ((JobLifetime)state!).RetireCompletedRoot(completed),
                this);
            _pendingSubmissions--;
            if (_pendingSubmissions == 0)
                Monitor.PulseAll(_gate);
        }
    }

    void IJobSubmissionObserver.RollbackSubmission(JobHandle handle)
    {
        lock (_gate)
        {
            if (handle.Index == 0)
            {
                if (_pendingSubmissions <= 0)
                    throw new InvalidOperationException("Job lifetime submission accounting is unbalanced.");
                _pendingSubmissions--;
                if (_pendingSubmissions == 0)
                    Monitor.PulseAll(_gate);
                return;
            }

            RemoveRootUnderGate(handle);
        }
    }

    internal void CloseAndComplete(List<Exception> exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        JobHandle currentScope = JobSystem.GetCurrentScope();
        if (currentScope.Index != 0)
        {
            throw new InvalidOperationException(
                $"{_ownerName} cannot be closed from a running Job callback.");
        }

        JobHandle[]? roots = null;
        int rootCount = 0;
        lock (_gate)
        {
            if (_state == Closed)
                return;

            if (_state == Closing)
            {
                while (_state != Closed)
                    Monitor.Wait(_gate);
                return;
            }

            _state = Closing;
            while (_pendingSubmissions != 0)
                Monitor.Wait(_gate);
            RetireObservedRootsUnderGate();
            rootCount = _roots.Count;
            if (rootCount != 0)
            {
                roots = ArrayPool<JobHandle>.Shared.Rent(rootCount);
                for (int i = 0; i < rootCount; i++)
                    roots[i] = _roots[i];
            }
        }

        try
        {
            CompleteRoots(roots, rootCount, exceptions);
        }
        finally
        {
            ReturnRoots(roots, rootCount);
            lock (_gate)
            {
                _roots.Clear();
                _state = Closed;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// Waits every root admitted before this call while keeping the lifetime open for the
    /// owner's destruction callback. System teardown uses this boundary to prevent OnUpdate work
    /// from overlapping OnDestroy without invalidating the callback's scheduler admission.
    /// </summary>
    internal void CompleteCurrentRoots(List<Exception> exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        JobHandle currentScope = JobSystem.GetCurrentScope();
        if (currentScope.Index != 0)
        {
            throw new InvalidOperationException(
                $"{_ownerName} cannot be drained from a running Job callback.");
        }

        JobHandle[]? roots = null;
        int rootCount = 0;
        lock (_gate)
        {
            if (_state != Open)
                throw new ObjectDisposedException(_ownerName);

            while (_pendingSubmissions != 0)
                Monitor.Wait(_gate);
            RetireObservedRootsUnderGate();
            rootCount = _roots.Count;
            if (rootCount != 0)
            {
                roots = ArrayPool<JobHandle>.Shared.Rent(rootCount);
                for (int i = 0; i < rootCount; i++)
                    roots[i] = _roots[i];
            }
        }

        try
        {
            CompleteRoots(roots, rootCount, exceptions);
        }
        finally
        {
            ReturnRoots(roots, rootCount);
            lock (_gate)
                RetireObservedRootsUnderGate();
        }
    }

    private static void CompleteRoots(
        JobHandle[]? roots,
        int rootCount,
        List<Exception> exceptions)
    {
        if (roots is null)
            return;

        for (int i = 0; i < rootCount; i++)
        {
            try
            {
                roots[i].Complete();
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }
    }

    private static void ReturnRoots(JobHandle[]? roots, int rootCount)
    {
        if (roots is null)
            return;
        roots.AsSpan(0, rootCount).Clear();
        ArrayPool<JobHandle>.Shared.Return(roots);
    }

    private bool IsOwnedScopeUnderGate(JobHandle scope)
    {
        for (int i = 0; i < _roots.Count; i++)
        {
            if (JobSystem.IsScopeDescendantOf(scope, _roots[i]))
                return true;
        }

        return false;
    }

    private void RetireCompletedRoot(JobHandle handle)
    {
        // Completion observers must never mark a fault as observed: the submitter still owns the
        // ordinary JobHandle.Complete exception contract. Successful roots can be forgotten at
        // once; faulted roots remain until the lifetime closes (or their state is independently
        // observed and recycled), so teardown can report otherwise-unobserved failures.
        if (JobSystem.NeedsLifetimeTracking(handle))
            return;

        lock (_gate)
            RemoveRootUnderGate(handle);
    }

    private void RetireObservedRootsUnderGate()
    {
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            if (!JobSystem.NeedsLifetimeTracking(_roots[i]))
                _roots.RemoveAt(i);
        }
    }

    private void RemoveRootUnderGate(JobHandle handle)
    {
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            JobHandle candidate = _roots[i];
            if (candidate.Index == handle.Index &&
                candidate.Version == handle.Version &&
                candidate.Generation == handle.Generation)
            {
                _roots.RemoveAt(i);
                return;
            }
        }
    }
}

internal static class JobSubmissionTracker
{
    [ThreadStatic]
    private static Frame? s_current;

    internal static JobSubmissionScope Enter(IJobSubmissionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var frame = new Frame(observer, s_current, Environment.CurrentManagedThreadId);
        s_current = frame;
        return new JobSubmissionScope(frame);
    }

    internal static void Exit(Frame frame)
    {
        if (!ReferenceEquals(s_current, frame) ||
            frame.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "Job submission scopes must be released in nesting order by their owning thread.");
        }

        s_current = frame.Previous;
    }

    internal static JobSubmissionReservation Begin(
        Scheduler scheduler,
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        JobHandle currentScope = scheduler.GetCurrentScope();
        var reservation = new JobSubmissionReservation();
        try
        {
            for (Frame? frame = s_current; frame is not null; frame = frame.Previous)
                reservation.Add(frame.Observer, currentScope);

            for (int i = 0; i < accesses.Length; i++)
            {
                IJobSubmissionObserver? observer = scheduler.GetSubmissionObserver(accesses[i]);
                if (observer is not null)
                    reservation.Add(observer, currentScope);
            }

            return reservation;
        }
        catch
        {
            reservation.Rollback();
            throw;
        }
    }

    internal sealed class Frame
    {
        internal Frame(
            IJobSubmissionObserver observer,
            Frame? previous,
            int ownerThreadId)
        {
            Observer = observer;
            Previous = previous;
            OwnerThreadId = ownerThreadId;
        }

        internal IJobSubmissionObserver Observer { get; }

        internal Frame? Previous { get; }

        internal int OwnerThreadId { get; }
    }
}

internal struct JobSubmissionReservation
{
    private Entry _first;
    private Entry _second;
    private Entry _third;
    private Entry _fourth;
    private List<Entry>? _overflow;
    private int _count;
    private JobHandle _boundHandle;

    internal void Add(IJobSubmissionObserver observer, JobHandle currentScope)
    {
        if (Contains(observer))
            return;

        bool reserved = observer.TryBeginSubmission(currentScope);
        var entry = new Entry(observer, reserved);
        try
        {
            Store(entry);
        }
        catch
        {
            if (reserved)
                observer.RollbackSubmission(default);
            throw;
        }
    }

    internal void Bind(JobHandle handle)
    {
        _boundHandle = handle;
        for (int i = 0; i < _count; i++)
        {
            Entry entry = Get(i);
            if (entry.Reserved)
                entry.Observer.CommitSubmission(handle);
        }
    }

    internal void Rollback()
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            Entry entry = Get(i);
            if (entry.Reserved)
                entry.Observer.RollbackSubmission(_boundHandle);
        }
    }

    private bool Contains(IJobSubmissionObserver observer)
    {
        for (int i = 0; i < _count; i++)
        {
            if (ReferenceEquals(Get(i).Observer, observer))
                return true;
        }

        return false;
    }

    private void Store(Entry entry)
    {
        switch (_count)
        {
            case 0:
                _first = entry;
                break;
            case 1:
                _second = entry;
                break;
            case 2:
                _third = entry;
                break;
            case 3:
                _fourth = entry;
                break;
            default:
                (_overflow ??= []).Add(entry);
                break;
        }

        _count++;
    }

    private readonly Entry Get(int index) => index switch
    {
        0 => _first,
        1 => _second,
        2 => _third,
        3 => _fourth,
        _ => _overflow![index - 4],
    };

    private readonly record struct Entry(
        IJobSubmissionObserver Observer,
        bool Reserved);
}
