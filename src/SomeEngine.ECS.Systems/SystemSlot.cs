namespace SomeEngine.ECS.Systems;

using SomeEngine.Job;

/// <summary>
/// Per-system runtime state owned by <see cref="SystemGroup{TContext}"/> and its context driver.
/// </summary>
public struct SystemSlot
{
    private JobLifetime? _jobLifetime;

    public int Index;
    public bool Created;
    public bool Enabled;
    public uint LastSystemVersion;
    public uint CurrentSystemVersion;

    internal void InitializeJobLifetime()
    {
        _jobLifetime ??= new JobLifetime($"SystemSlot[{Index}]");
    }

    internal void ResetJobLifetime()
    {
        _jobLifetime = new JobLifetime($"SystemSlot[{Index}]");
    }

    internal void PrepareJobLifetimeForDestroy()
    {
        if (_jobLifetime is null || !_jobLifetime.IsOpen)
            ResetJobLifetime();
    }

    internal readonly JobLifetime RequireJobLifetime() =>
        _jobLifetime ?? throw new InvalidOperationException(
            "SystemSlot is not owned by a SystemGroup.");

    internal readonly int TrackedJobRootCount => RequireJobLifetime().TrackedRootCount;
}

