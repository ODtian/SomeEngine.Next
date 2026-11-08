namespace SomeEngine.Job;

public readonly struct JobHandle
{
    internal readonly Scheduler? Scheduler;
    internal readonly int Index;
    internal readonly int Version;
    internal readonly long Generation;

    internal JobHandle(int index, int version, long generation, Scheduler scheduler)
    {
        Scheduler = scheduler;
        Index = index;
        Version = version;
        Generation = generation;
    }

    public bool IsCompleted => Scheduler?.IsCompleted(this) ?? true;

    public void Complete()
    {
        Scheduler?.Complete(this);
    }

    /// <summary>Completes the work and its descendants, with caller execution on a zero-worker coordinating thread.</summary>
    /// <remarks>Job callbacks and schedulers with workers continue through asynchronous completion notifications.</remarks>
    public async ValueTask CompleteAsync()
    {
        if (Scheduler is { WorkerCount: 0 } && !JobExecutionContext.IsActive)
            Complete();
        else
            await WaitAsync().ConfigureAwait(false);
    }

    /// <summary>Cancels this wait while the shared work and its resource use continue to completion.</summary>
    public ValueTask WaitAsync(CancellationToken cancellationToken) => cancellationToken.CanBeCanceled
        ? new ValueTask(WaitAsync().AsTask().WaitAsync(cancellationToken))
        : WaitAsync();

    /// <summary>Waits for the work and its descendants, propagating their completion result.</summary>
    public ValueTask WaitAsync()
    {
        JobHandleAwaiter completion = this.GetAwaiter();
        if (completion.IsCompleted)
        {
            try
            {
                completion.GetResult();
                return ValueTask.CompletedTask;
            }
            catch (OperationCanceledException canceled)
            {
                CancellationToken token = canceled.CancellationToken;
                return ValueTask.FromCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            }
            catch (Exception error)
            {
                return ValueTask.FromException(error);
            }
        }
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.OnCompleted(() =>
        {
            try { completion.GetResult(); result.SetResult(); }
            catch (OperationCanceledException canceled) { result.SetCanceled(canceled.CancellationToken); }
            catch (Exception error) { result.SetException(error); }
        });
        return new ValueTask(result.Task);
    }
}



