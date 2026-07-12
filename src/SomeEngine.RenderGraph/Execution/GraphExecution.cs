namespace SomeEngine.RenderGraph;

public sealed class GraphExecution
{
    private readonly IDevice _device;
    private readonly GpuCompletionSet _completionSet;
    private readonly ResourceContinuity? _continuity;
    private readonly long[] _exportTickets;
    private ResourceExport[]? _exports;

    internal GraphExecution(
        IDevice device,
        GpuCompletion[] completions,
        ResourceContinuity? continuity = null,
        long[]? exportTickets = null,
        Capture? capture = null)
    {
        _device = device;
        _completionSet = completions.Length == 0 ? GpuCompletionSet.Empty : new GpuCompletionSet(completions);
        _continuity = continuity;
        _exportTickets = exportTickets ?? [];
        Capture = capture;
    }

    public GpuCompletionSet CompletionSet => _completionSet;
    public IReadOnlyList<GpuCompletion> Completions => _completionSet.Completions;
    public Capture? Capture { get; }

    /// <summary>
    /// Publishes exported ownership once all producer completions have passed. Access before GPU
    /// completion fails closed; call <see cref="Wait"/> first when blocking publication is desired.
    /// </summary>
    public IReadOnlyList<ResourceExport> Exports
    {
        get
        {
            if (_exports is not null) return _exports;
            if (_exportTickets.Length == 0) return _exports = [];
            if (_continuity is null) throw new InvalidOperationException("This execution has no export owner.");
            return _exports = _continuity.TransferExports(_exportTickets);
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        long started = Environment.TickCount64;
        foreach (GpuCompletion completion in _completionSet.Completions)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? timeout
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            // A zero timeout is a poll, not permission to skip the completion query because the
            // monotonic clock advanced between two instructions. Clamp exhausted budgets to zero
            // so already-complete queues still succeed deterministically.
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!_device.Wait(completion, remaining)) return false;
        }
        return true;
    }
}

/// <summary>
/// Reports an invocation that failed after at least one queue submission was published. The
/// completion set can be waited for lifetime recovery, but the graph did not establish every
/// imported resource's requested final state.
/// </summary>
public sealed class GraphSubmissionException : Exception
{
    internal GraphSubmissionException(GpuCompletionSet publishedCompletions, Exception innerException)
        : base(
            "Render-graph execution failed after partial submission; published completions remain valid, but imported final-state contracts are not established.",
            innerException)
    {
        PublishedCompletions = publishedCompletions;
    }

    public GpuCompletionSet PublishedCompletions { get; }
}
