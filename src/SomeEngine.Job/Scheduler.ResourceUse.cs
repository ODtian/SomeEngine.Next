namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    internal void AddUse(JobHandle handle, IJobSubmissionObserver user) =>
        _completion.AddUse(handle, user);

    internal void RemoveUse(JobHandle handle, IJobSubmissionObserver user) =>
        _completion.RemoveUse(handle, user);

    internal bool Uses(JobHandle handle, IJobSubmissionObserver user)
    {
        if (!ReferenceEquals(handle.Scheduler, this))
            return false;
        while (handle.Index != 0)
        {
            if (_completion.HasUse(handle, user))
                return true;
            handle = _completion.GetParent(handle);
        }
        return false;
    }

    internal bool HelpComplete() => _workQueue.TryExecuteOne(wait: false);

    internal JobResourceToken CreateResourceToken(string name, IJobSubmissionObserver user)
    {
        JobResourceToken token = CreateResourceToken(name);
        try
        {
            lock (_submissionObserverGate)
                _submissionObservers.Add(
                    new SubmissionResourceIdentity(ResourceKind.Token, token.Id, token.Version, token.Generation),
                    user);
            return token;
        }
        catch
        {
            ReleaseResourceToken(token);
            throw;
        }
    }

    private sealed partial class CompletionStore
    {
        internal void AddUse(JobHandle handle, IJobSubmissionObserver user)
        {
            CompletionState state = GetState(handle)
                ?? throw new InvalidOperationException("The submitted Job has no completion data.");
            lock (state.Sync)
            {
                state.Users ??= new HashSet<IJobSubmissionObserver>(ReferenceEqualityComparer.Instance);
                state.Users.Add(user);
            }
        }

        internal void RemoveUse(JobHandle handle, IJobSubmissionObserver user)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
                return;
            lock (state.Sync)
            {
                if (state.InUse && state.Version == handle.Version)
                    state.Users?.Remove(user);
            }
        }

        internal bool HasUse(JobHandle handle, IJobSubmissionObserver user)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
                return false;
            lock (state.Sync)
                return state.InUse && state.Version == handle.Version && state.Users?.Contains(user) == true;
        }
    }
}
