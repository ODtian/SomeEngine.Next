namespace SomeEngine.RenderGraph;

/// <summary>
/// Reports an invocation that failed after one or more queue submissions were published. Only
/// those already-published completions are exposed; no extraction result exists for a failure.
/// </summary>
public sealed class RenderGraphExecutionException : Exception
{
    internal RenderGraphExecutionException(
        QueueCompletion[] publishedFences,
        Exception innerException)
        : base(
            "Render-graph execution failed after partial submission; published completions remain valid, but imported final states are not established.",
            innerException)
    {
        PublishedFences = publishedFences;
    }

    public QueueCompletion[] PublishedFences { get; }
}
