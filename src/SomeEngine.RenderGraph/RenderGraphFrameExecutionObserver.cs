namespace SomeEngine.RenderGraph;

/// <summary>Receives the accepted GPU submissions when a frame finishes or is canceled.</summary>
public interface IRenderGraphFrameExecutionObserver
{
    void OnRenderGraphFrameFinished(RenderGraphFrameResult result);
}

/// <summary>Pass submission results, available only during the frame completion callback.</summary>
public readonly ref struct RenderGraphFrameResult
{
    private readonly RenderGraphFrameState? _frame;

    internal RenderGraphFrameResult(RenderGraphFrameState? frame) => _frame = frame;

    /// <summary>Accepted submissions, including work accepted before a later failure.</summary>
    public ReadOnlySpan<QueueCompletion> SubmittedCompletions =>
        _frame is null ? [] : _frame.SubmittedCompletions;

    /// <summary>Returns a completion only if this pass was submitted in this frame.</summary>
    public bool TryGetPassCompletion(GraphPassId pass, out QueueCompletion completion)
    {
        if (_frame is not null)
            return _frame.TryGetPassCompletion(pass, out completion);
        completion = default;
        return false;
    }
}
