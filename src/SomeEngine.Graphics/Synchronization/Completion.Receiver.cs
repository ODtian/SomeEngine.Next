namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    bool IsComplete(in QueueCompletion completion);
    WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout);
    ValueTask WaitAsync(QueueCompletion completion) => new(Task.Run(() =>
    {
        if (WaitCpu(in completion, Timeout.InfiniteTimeSpan) != WaitStatus.Completed)
            throw new GraphicsException(GraphicsError.NativeFailure, "Waiting for the queue completion failed.");
    }));
}
