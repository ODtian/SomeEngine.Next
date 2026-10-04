using System.Runtime.CompilerServices;

namespace SomeEngine.Views;

/// <summary>Waits for the next rebuild phase of the current view's World.</summary>
public readonly struct FrameAwaitable : ICriticalNotifyCompletion
{
    private readonly BuildQueue _queue;
    private readonly CancellationToken _cancel;

    internal FrameAwaitable(BuildQueue queue, CancellationToken cancel) => (_queue, _cancel) = (queue, cancel);

    public FrameAwaitable GetAwaiter() => this;
    public bool IsCompleted => false;
    public void GetResult() => _cancel.ThrowIfCancellationRequested();
    public void OnCompleted(Action continuation) => _queue.NextFrame(continuation);
    public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
}
