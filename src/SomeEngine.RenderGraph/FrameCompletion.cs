using System.Diagnostics;

namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph
{
    private readonly Queue<ulong> _submittedFrames = new();

    public ulong LastSubmittedFrame { get; private set; }
    public ulong LastCompletedFrame { get; private set; }

    internal void RecordSubmission(ulong identity)
    {
        LastSubmittedFrame = identity;
        _submittedFrames.Enqueue(identity);
    }

    /// <summary>Queries the GPU work associated with a submitted frame.</summary>
    public bool IsFrameComplete(ulong frameIdentity)
    {
        EnsureCompletionQueries();
        if (frameIdentity <= LastCompletedFrame) return true;
        foreach (FrameResources frame in _frameResources)
            if (frame.FrameIdentity == frameIdentity) return frame.IsReady(_backend);
        return frameIdentity <= LastSubmittedFrame;
    }

    /// <summary>Waits within the timeout for every queue used by the submitted frame.</summary>
    public WaitStatus WaitForFrame(ulong frameIdentity, TimeSpan timeout)
    {
        EnsureCompletionQueries();
        long started = Stopwatch.GetTimestamp();
        if (frameIdentity > LastCompletedFrame)
            foreach (FrameResources frame in _frameResources)
            {
                if (frame.FrameIdentity != frameIdentity) continue;
                foreach (QueueCompletion completion in frame.Completions)
                {
                    if (_backend.IsComplete(completion)) continue;
                    TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan ? timeout
                        : timeout - Stopwatch.GetElapsedTime(started);
                    if (remaining < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) remaining = TimeSpan.Zero;
                    WaitStatus status = _backend.WaitCpu(completion, remaining);
                    if (status != WaitStatus.Completed) return status;
                }
                break;
            }
        CollectCompleted();
        return WaitStatus.Completed;
    }

    private void CollectCompletedFrames()
    {
        while (_submittedFrames.TryPeek(out ulong identity) && IsFrameComplete(identity))
        {
            LastCompletedFrame = identity;
            _submittedFrames.Dequeue();
        }
    }
}
