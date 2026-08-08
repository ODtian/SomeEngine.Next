namespace SomeEngine.Job;

/// <summary>
/// Keeps a set of Job resources admitted to a synchronous, non-Job caller until disposal.
/// </summary>
/// <remarks>
/// This is an internal bridge primitive used by owner APIs such as ECS World. It deliberately is
/// not a second public scheduling model: callers outside the Job assembly receive it only through
/// an owner-specific access coordinator.
/// </remarks>
internal readonly struct SynchronousResourceOwner : IDisposable
{
    private readonly Scheduler? _scheduler;
    private readonly JobHandle _handle;

    internal SynchronousResourceOwner(Scheduler scheduler, JobHandle handle)
    {
        _scheduler = scheduler;
        _handle = handle;
    }

    public void Dispose()
    {
        _scheduler?.ReleaseSynchronousAccess(_handle);
    }
}
