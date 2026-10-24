namespace SomeEngine.RenderGraph;

/// <summary>Receives accepted GPU submissions and frame completion results.</summary>
public interface IRenderGraphFrameExecutionObserver
{
    /// <summary>Receives accepted submissions while later frame work can still be recorded and submitted.</summary>
    void OnRenderGraphSubmitted(RenderGraphFrameResult result) { }

    void OnRenderGraphFrameFinished(RenderGraphFrameResult result);
}

/// <summary>Pass submission results available during the execution callbacks.</summary>
public readonly ref struct RenderGraphFrameResult
{
    private readonly RenderGraphFrameState? _frame;

    internal RenderGraphFrameResult(RenderGraphFrameState? frame) => _frame = frame;

    /// <summary>Accepted submissions, including work accepted before a later failure.</summary>
    public ReadOnlySpan<QueueCompletion> SubmittedCompletions =>
        _frame is null ? [] : _frame.SubmittedCompletions;

    /// <summary>
    /// Resource states after the accepted submissions, including a partially submitted frame.
    /// Returns an empty span when no work was submitted or the resource is unavailable.
    /// Copy these states when retaining an external resource for a later frame.
    /// </summary>
    public ReadOnlySpan<BufferBoundaryState> GetBufferStates(GraphBufferId buffer) =>
        _frame is null ? [] : _frame.GetBufferStates(buffer);

    /// <inheritdoc cref="GetBufferStates"/>
    public ReadOnlySpan<TextureBoundaryState> GetTextureStates(GraphTextureId texture) =>
        _frame is null ? [] : _frame.GetTextureStates(texture);

    /// <summary>Returns actual states for an external resource used by this frame.</summary>
    public ReadOnlySpan<BufferBoundaryState> GetBufferStates(Buffer buffer) =>
        _frame is null ? [] : _frame.GetBufferStates(buffer);

    /// <inheritdoc cref="GetBufferStates(Buffer)"/>
    public ReadOnlySpan<TextureBoundaryState> GetTextureStates(Texture texture) =>
        _frame is null ? [] : _frame.GetTextureStates(texture);

    /// <summary>Returns a completion only if this pass was submitted in this frame.</summary>
    public bool TryGetPassCompletion(GraphPassId pass, out QueueCompletion completion)
    {
        if (_frame is not null)
            return _frame.TryGetPassCompletion(pass, out completion);
        completion = default;
        return false;
    }
}
