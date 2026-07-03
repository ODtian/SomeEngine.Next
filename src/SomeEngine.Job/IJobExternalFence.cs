namespace SomeEngine.Job;

public interface IJobExternalFence
{
    /// <summary>
    /// Gets whether the external fence has signaled. This is a query only; completion bridging uses
    /// <see cref="OnSignaled"/> as the authoritative registration operation.
    /// </summary>
    bool IsSignaled { get; }

    /// <summary>
    /// Registers a continuation that is invoked exactly once when the fence signals. Implementations
    /// must invoke the continuation during registration if the fence has already signaled, must invoke
    /// callbacks outside provider locks, and must not throw after accepting or invoking a continuation.
    /// </summary>
    void OnSignaled(Action<object?> continuation, object? state);
}



