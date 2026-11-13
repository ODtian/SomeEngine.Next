namespace SomeEngine.RenderGraph;

/// <summary>Updates feature state after every command in a frame has been submitted.</summary>
public interface IRenderGraphFrameExecutionObserver
{
    void OnFrameSubmitted(in RenderGraphSubmission submission);
}

/// <summary>The submitted frame and the readbacks it produced.</summary>
public readonly ref struct RenderGraphSubmission
{
    private readonly RenderGraphFrameState _frame;

    internal RenderGraphSubmission(RenderGraphFrameState frame) => _frame = frame;

    public ulong FrameIdentity => _frame.Identity;

    public bool TryGetReadbackCompletion(GraphReadbackId readback, out ulong completedFrame) =>
        _frame.TryGetReadbackCompletion(readback, out completedFrame);
}
