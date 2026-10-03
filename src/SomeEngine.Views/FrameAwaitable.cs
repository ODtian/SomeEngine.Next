using System.Runtime.CompilerServices;

namespace SomeEngine.Views;

/// <summary>Waits for the next rebuild phase of the current view's World.</summary>
public readonly struct FrameAwaitable : ICriticalNotifyCompletion
{
    private readonly BuildQueue _queue;

    internal FrameAwaitable(BuildQueue queue) => _queue = queue;

    public FrameAwaitable GetAwaiter() => this;
    public bool IsCompleted => false;
    public void GetResult() { }
    public void OnCompleted(Action continuation) => _queue.NextFrame(continuation);
    public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
}
