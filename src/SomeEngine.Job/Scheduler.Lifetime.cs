namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    private readonly object _shutdownGate = new();
    private int _shutdownState;
    private int _shutdownThread;
    private int _submittingThreads;

    private Submission EnterSubmission()
    {
        lock (_shutdownGate)
        {
            // Children remain part of an already accepted task while shutdown drains its tree.
            bool child = ReferenceEquals(s_executing, this) && s_currentScope.Index != 0;
            ObjectDisposedException.ThrowIf(
                _shutdownState == 2 || (_shutdownState == 1 && !child), this);
            _submittingThreads++;
            return new Submission(this);
        }
    }

    private void ExitSubmission()
    {
        lock (_shutdownGate)
        {
            _submittingThreads--;
            if (_submittingThreads == 0)
                Monitor.PulseAll(_shutdownGate);
        }
    }

    private void ValidateDependency(JobHandle dependency)
    {
        if (dependency.Scheduler is not null && !ReferenceEquals(dependency.Scheduler, this))
            throw new ArgumentException("A dependency must belong to this scheduler.", nameof(dependency));
    }

    /// <summary>
    /// Stops accepting unrelated submissions, waits for accepted tasks and their children,
    /// and then stops the worker threads. Unobserved task failures are reported after draining.
    /// </summary>
    public void Dispose()
    {
        if (s_executing is not null)
            throw new InvalidOperationException("A scheduler cannot be stopped from a Job callback.");

        int thread = Environment.CurrentManagedThreadId;
        lock (_shutdownGate)
        {
            if (_shutdownState == 2)
                return;
            if (_shutdownState == 1)
            {
                if (_shutdownThread == thread)
                    return;
                while (_shutdownState != 2)
                    Monitor.Wait(_shutdownGate);
                return;
            }
            _shutdownState = 1;
            _shutdownThread = thread;
            while (_submittingThreads != 0)
                Monitor.Wait(_shutdownGate);
        }

        List<Exception>? failures = null;
        try
        {
            // The completion store already knows the task forest. Do not maintain a second
            // scheduler-wide list of roots just to implement shutdown.
            foreach (JobHandle root in _completion.GetUnfinishedRoots())
            {
                if (root.IsCompleted && !NeedsLifetimeTracking(root))
                    continue;
                try { root.Complete(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
        }
        finally
        {
            try
            {
                _workQueue.Dispose();
                _streams.Clear();
                lock (_submissionObserverGate)
                    _submissionObservers.Clear();
            }
            finally
            {
                lock (_shutdownGate)
                {
                    _shutdownThread = 0;
                    _shutdownState = 2;
                    Monitor.PulseAll(_shutdownGate);
                }
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more Jobs failed before scheduler shutdown.", failures);
    }

    private readonly struct Submission(Scheduler scheduler) : IDisposable
    {
        public void Dispose() => scheduler.ExitSubmission();
    }

    private sealed partial class CompletionStore
    {
        internal JobHandle[] GetUnfinishedRoots()
        {
            List<JobHandle> roots = [];
            lock (_stateLock)
            {
                for (int index = 1; index < _nextStateIndex; index++)
                {
                    CompletionState? state = _states[index];
                    if (state is null)
                        continue;
                    lock (state.Sync)
                    {
                        if (state.InUse && state.Parent.Index == 0 &&
                            (!state.Completed || (state.Fault is not null && !state.FailureReported)))
                            roots.Add(new JobHandle(index, state.Version, _generation, _scheduler));
                    }
                }
            }
            return roots.ToArray();
        }
    }
}
