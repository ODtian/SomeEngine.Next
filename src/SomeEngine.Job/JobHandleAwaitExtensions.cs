using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

/// <summary>Allows asynchronous methods to await a Job's completion.</summary>
public static class JobHandleAwaitExtensions
{
    public static JobHandleAwaiter GetAwaiter(this JobHandle handle) => new(handle);
}

/// <summary>Observes Job completion through the JobSystem completion callbacks.</summary>
public readonly struct JobHandleAwaiter : ICriticalNotifyCompletion
{
    private readonly ExceptionDispatchInfo? _fault;
    private readonly JobCompletionResult? _result;

    internal JobHandleAwaiter(JobHandle handle)
    {
        if (handle.Scheduler is { } scheduler)
            _result = scheduler.ObserveCompletion(handle, out _fault);
    }

    public bool IsCompleted => _result?.IsCompleted ?? true;

    /// <summary>Completes the Job and observes its result.</summary>
    public void GetResult()
    {
        _result?.Complete();
        _fault?.Throw();
    }

    /// <summary>Runs the continuation on the registering, completing, or signaling thread.</summary>
    public void OnCompleted(Action continuation)
    {
        if (_result is { } result)
            result.OnCompleted(continuation);
        else
            JobSystem.OnCompleted(default, static (_, state) => ((Action)state!)(), continuation);
    }

    public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
}

internal sealed class JobCompletionResult
{
    private readonly JobHandle _handle;
    private readonly Lock _sync = new();
    private ExceptionDispatchInfo? _fault;
    private Action? _continuation;
    private bool _completed;

    internal JobCompletionResult(JobHandle handle)
    {
        _handle = handle;
        JobSystem.OnCompleted(handle, static (completed, state) =>
            ((JobCompletionResult)state!).SetCompleted(completed), this);
    }

    internal bool IsCompleted => Volatile.Read(ref _completed);

    internal void Complete()
    {
        if (IsCompleted)
            _fault?.Throw();
        else
            _handle.Complete();
    }

    internal void OnCompleted(Action continuation)
    {
        lock (_sync)
        {
            if (!_completed)
            {
                _continuation = continuation;
                return;
            }
        }
        JobSystem.OnCompleted(default, static (_, state) => ((Action)state!)(), continuation);
    }

    private void SetCompleted(JobHandle handle)
    {
        handle.Scheduler!.TryGetCompletionResult(handle, out ExceptionDispatchInfo? fault);
        Action? continuation;
        lock (_sync)
        {
            _fault = fault;
            continuation = _continuation;
            _continuation = null;
            Volatile.Write(ref _completed, true);
        }
        continuation?.Invoke();
    }
}
