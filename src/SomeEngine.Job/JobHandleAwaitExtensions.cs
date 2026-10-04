using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

/// <summary>Allows asynchronous methods to await a Job's completion.</summary>
public static class JobHandleAwaitExtensions
{
    public static JobHandleAwaiter GetAwaiter(this JobHandle handle) => new(handle);
}

/// <summary>Observes Job completion through the JobSystem completion callbacks.</summary>
public readonly struct JobHandleAwaiter : ICriticalNotifyCompletion
{
    private readonly JobHandle _handle;

    internal JobHandleAwaiter(JobHandle handle) => _handle = handle;

    public bool IsCompleted => _handle.IsCompleted;

    /// <summary>Completes the Job and observes its result.</summary>
    public void GetResult() => _handle.Complete();

    /// <summary>Runs the continuation on the registering, completing, or signaling thread.</summary>
    public void OnCompleted(Action continuation) =>
        JobSystem.OnCompleted(_handle, static (_, state) => ((Action)state!)(), continuation);

    public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
}
