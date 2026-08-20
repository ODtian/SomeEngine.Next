namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    bool IsComplete(in QueueCompletion completion);
    WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout);
}
