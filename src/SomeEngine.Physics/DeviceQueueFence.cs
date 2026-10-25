using System.Runtime.ExceptionServices;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Bridges an actual RHI completion using the existing scheduler's waiting work.</summary>
internal sealed class DeviceQueueFence(Scheduler scheduler, IGraphicsBackend backend, QueueCompletion completion)
    : IJobExternalFence
{
    private ExceptionDispatchInfo? _failure;
    private int _signaled;
    public bool IsSignaled => Volatile.Read(ref _signaled) != 0;

    public void OnSignaled(Action<object?> continuation, object? state)
    {
        if (backend.IsComplete(in completion))
        {
            Volatile.Write(ref _signaled, 1);
            continuation(state);
            return;
        }
        scheduler.Schedule(new WaitForDevice(this, continuation, state));
    }

    public void GetResult() => _failure?.Throw();

    private void Wait(Action<object?> continuation, object? state)
    {
        try
        {
            if (backend.WaitCpu(in completion, TimeSpan.FromSeconds(60)) != WaitStatus.Completed)
                throw new TimeoutException("Device collision work did not complete within its wait interval.");
        }
        catch (Exception failure) { _failure = ExceptionDispatchInfo.Capture(failure); }
        Volatile.Write(ref _signaled, 1);
        continuation(state);
    }

    private readonly struct WaitForDevice(DeviceQueueFence fence, Action<object?> continuation, object? state) : IJob
    {
        public void Execute() => fence.Wait(continuation, state);
    }
}
