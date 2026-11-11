namespace SomeEngine.ECS.Systems;

using SomeEngine.Job;

/// <summary>
/// Per-system execution state owned by <see cref="SystemGroup{TContext}"/> and its context driver.
/// </summary>
public sealed partial class SystemState
{
    private JobLifetime? _jobLifetime;


    internal void InitializeJobLifetime()
    {
        _jobLifetime ??= new JobLifetime($"SystemState[{Index}]");
    }

    internal void ResetJobLifetime()
    {
        _jobLifetime = new JobLifetime($"SystemState[{Index}]");
    }

    internal void PrepareJobLifetimeForDestroy()
    {
        if (_jobLifetime is null || !_jobLifetime.IsOpen)
            ResetJobLifetime();
    }

    internal JobLifetime RequireJobLifetime() =>
        _jobLifetime ?? throw new InvalidOperationException(
            "SystemState is not owned by a SystemGroup.");

}
