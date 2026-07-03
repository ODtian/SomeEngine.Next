namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private sealed partial class CompletionStore
    {
        public bool SignalFence(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse
                    || state.Version != handle.Version
                    || state.Completed
                    || state.PendingWork == 0)
                {
                    return false;
                }

                state.PendingWork--;
                return true;
            }
        }

        public bool AddExternal(JobHandle handle, ExternalCompletionContinuation continuation)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version)
                {
                    return false;
                }

                if (state.Completed)
                {
                    return false;
                }

                state.ExternalContinuations ??= [];
                state.ExternalContinuations.Add(continuation);
                return true;
            }
        }
    }
}



