using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private readonly struct CompletionLease
    {
        internal readonly CompletionState? State;
        internal readonly JobHandle Handle;

        internal CompletionLease(CompletionState state, JobHandle handle)
        {
            State = state;
            Handle = handle;
        }
    }

    private sealed partial class CompletionStore :
        ICompletionStore,
        IDependencyStore,
        IExecutionStore,
        IScopeStore,
        IFenceStore
    {
        private const string CorruptCompletionStatePoolMessage = "Completion state pool is corrupt.";

        private readonly RuntimeCounters _counters;
        private readonly long _generation;
        private readonly int _maxCompletionStates;
        private readonly Lock _stateLock = new();
        private readonly CompletionState?[] _states;
        private readonly Stack<int> _freeStates = new();
        private readonly Stack<int> _observedFaultedStates = new();
        private int _nextStateIndex;

        internal CompletionStore(JobRuntimeConfig config, RuntimeCounters counters, long generation)
        {
            _counters = counters;
            _generation = generation;
            _maxCompletionStates = config.MaxCompletionStates;
            _states = new CompletionState?[_maxCompletionStates + 1];
            _nextStateIndex = 1;
        }

        public bool IsCompleted(JobHandle handle)
        {
            if (handle.Index == 0)
            {
                return true;
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return true;
            }

            return IsCompleted(state, handle);
        }

        public bool IsCompleted(CompletionLease lease)
        {
            return lease.State is null || IsCompleted(lease.State, lease.Handle);
        }

        public bool TryBeginWait(JobHandle handle, out CompletionLease lease)
        {
            CompletionState? candidate = GetState(handle);
            if (candidate is null)
            {
                lease = default;
                return false;
            }

            lock (candidate.Sync)
            {
                if (!candidate.InUse || candidate.Version != handle.Version)
                {
                    lease = default;
                    return false;
                }

                candidate.ActiveCompleters++;
                lease = new CompletionLease(candidate, handle);
                return true;
            }
        }

        public void EndWait(CompletionLease lease)
        {
            CompletionState? state = lease.State;
            if (state is null)
            {
                return;
            }

            bool queueForReuse = false;
            lock (state.Sync)
            {
                if (!state.InUse || state.Version != lease.Handle.Version || state.ActiveCompleters == 0)
                {
                    return;
                }

                state.ActiveCompleters--;
                if (state.ActiveCompleters == 0
                    && state.Completed
                    && state.Fault is not null
                    && state.FaultObserved
                    && !state.QueuedForFaultReuse)
                {
                    state.QueuedForFaultReuse = true;
                    queueForReuse = true;
                }
            }

            if (queueForReuse)
            {
                lock (_stateLock)
                {
                    _observedFaultedStates.Push(lease.Handle.Index);
                }
            }
        }

        public JobHandle CreateState(
            int pendingWork,
            int pendingDependencies,
            bool scheduleDependenciesSealed)
        {
            lock (_stateLock)
            {
                CompletionState state;
                int index;
                if (_freeStates.Count > 0)
                {
                    index = _freeStates.Pop();
                    state = _states[index] ?? throw new InvalidOperationException(CorruptCompletionStatePoolMessage);
                }
                else if (TryRentObservedFaultedState(out index, out state))
                {
                }
                else
                {
                    if (_nextStateIndex > _maxCompletionStates)
                    {
                        throw new InvalidOperationException(
                            $"Completion state capacity exhausted ({_maxCompletionStates}).");
                    }

                    index = _nextStateIndex++;
                    state = new CompletionState();
                    _states[index] = state;
                    _counters.CompletionStateHighWater(index);
                }

                lock (state.Sync)
                {
                    state.Reset(pendingWork, pendingDependencies, scheduleDependenciesSealed);
                    return new JobHandle(index, state.Version, _generation);
                }
            }
        }

        public ScopeToken CancelState(JobHandle handle)
        {
            lock (_stateLock)
            {
                if (handle.Index <= 0 || handle.Index >= _states.Length)
                {
                    return default;
                }

                CompletionState? state = _states[handle.Index];
                if (state is null)
                {
                    return default;
                }

                lock (state.Sync)
                {
                    if (!state.InUse || state.Version != handle.Version)
                    {
                        return default;
                    }

                    ScopeToken parent = state.Parent;
                    state.Release();
                    _freeStates.Push(handle.Index);
                    return parent;
                }
            }
        }

        public void RecordFault(JobHandle handle, ExceptionDispatchInfo fault)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return;
            }

            lock (state.Sync)
            {
                if (state.InUse && state.Version == handle.Version)
                {
                    state.Fault ??= fault;
                }
            }
        }

        public void CancelWithFault(JobHandle handle, ExceptionDispatchInfo fault)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return;
            }

            lock (state.Sync)
            {
                if (state.InUse && state.Version == handle.Version && !state.Completed)
                {
                    state.Fault ??= fault;
                    state.PendingWork = 0;
                }
            }
        }

        public bool TryGetCompletedFault(
            JobHandle handle,
            bool markObserved,
            out ExceptionDispatchInfo? fault)
        {
            fault = null;
            if (handle.Index == 0)
            {
                return true;
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return true;
            }

            return TryGetCompletedFault(new CompletionLease(state, handle), markObserved, out fault);
        }

        public bool TryGetCompletedFault(
            CompletionLease lease,
            bool markObserved,
            out ExceptionDispatchInfo? fault)
        {
            fault = null;
            CompletionState? state = lease.State;
            if (state is null)
            {
                return true;
            }

            JobHandle handle = lease.Handle;
            if (!Volatile.Read(ref state.InUse) || Volatile.Read(ref state.Version) != handle.Version)
            {
                return true;
            }

            if (Volatile.Read(ref state.Completed) && Volatile.Read(ref state.Fault) is null)
            {
                return true;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version)
                {
                    return true;
                }

                if (!state.Completed)
                {
                    return false;
                }

                fault = state.Fault;
                if (markObserved && fault is not null)
                {
                    state.FaultObserved = true;
                }

                return true;
            }
        }

        public void ReleaseSuccess(JobHandle handle)
        {
            lock (_stateLock)
            {
                if (handle.Index <= 0 || handle.Index >= _states.Length)
                {
                    return;
                }

                CompletionState? state = _states[handle.Index];
                if (state is null)
                {
                    return;
                }

                lock (state.Sync)
                {
                    if (!state.InUse
                        || state.Version != handle.Version
                        || !state.Completed
                        || state.Fault is not null)
                    {
                        return;
                    }

                    state.Release();
                    _freeStates.Push(handle.Index);
                }
            }
        }

        private CompletionState? GetState(JobHandle handle)
        {
            if (handle.Generation != _generation || (uint)handle.Index >= (uint)_states.Length)
            {
                return null;
            }

            return _states[handle.Index];
        }

        private static bool IsCompleted(CompletionState state, JobHandle handle)
        {
            if (!Volatile.Read(ref state.InUse)
                || Volatile.Read(ref state.Version) != handle.Version
                || Volatile.Read(ref state.Completed))
            {
                return true;
            }

            lock (state.Sync)
            {
                return !state.InUse || state.Version != handle.Version || state.Completed;
            }
        }

        private bool TryRentObservedFaultedState(out int index, out CompletionState state)
        {
            while (_observedFaultedStates.Count > 0)
            {
                int candidateIndex = _observedFaultedStates.Pop();
                CompletionState? candidate = _states[candidateIndex];
                if (candidate is null)
                {
                    continue;
                }

                lock (candidate.Sync)
                {
                    if (!candidate.InUse
                        || !candidate.Completed
                        || candidate.Fault is null
                        || !candidate.FaultObserved
                        || candidate.ActiveCompleters != 0)
                    {
                        candidate.QueuedForFaultReuse = false;
                        continue;
                    }

                    candidate.QueuedForFaultReuse = false;
                    candidate.Release();
                    index = candidateIndex;
                    state = candidate;
                    return true;
                }
            }

            index = 0;
            state = null!;
            return false;
        }
    }
}



