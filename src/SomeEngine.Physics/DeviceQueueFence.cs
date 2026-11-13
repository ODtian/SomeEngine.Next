using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Connects the RHI completion to a Job dependency while retaining its resource accesses.</summary>
internal sealed class DeviceQueueFence : IJobExternalFence
{
    private readonly Task _completion;
    internal DeviceQueueFence(Scheduler scheduler, IGraphicsBackend backend, QueueCompletion completion) =>
        _completion = backend.WaitAsync(completion).AsTask();
    public bool IsSignaled => _completion.IsCompleted;
    public void OnSignaled(Action<object?> continuation, object? state) =>
        _completion.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => continuation(state));
    public void GetResult() => _completion.GetAwaiter().GetResult();
}
