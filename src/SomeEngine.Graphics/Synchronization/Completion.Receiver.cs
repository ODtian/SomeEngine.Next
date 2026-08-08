using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    bool IsComplete(in QueueCompletion completion);
    WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsComplete(in QueueCompletion completion) => Receiver.IsComplete(completion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout) =>
        Receiver.WaitCpu(completion, timeout);
}
