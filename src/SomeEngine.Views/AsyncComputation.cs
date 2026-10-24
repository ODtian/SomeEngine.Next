namespace SomeEngine.Views;

/// <summary>A keyed child calculation whose waits share the view rebuild queue.</summary>
internal abstract class AsyncComputation(ulong key, int occurrence)
{
    internal ulong Key { get; } = key;
    internal int Occurrence { get; } = occurrence;
    internal Computation Computation { get; } = Scope.AddComputation(null);

    internal abstract BuildTask Run(CancellationToken cancel);

    protected void Restart()
    {
        if (Computation.ScopeData is { Suspended: true } previous)
        {
            previous.Frame = Scope.Frame;
            previous.Queue.Cancel(previous, restore: true);
            Computation.ScopeData = null;
        }
        Computation.Execute(Scope.Frame);
    }
}

internal sealed class LaunchComputation<TKey> : AsyncComputation
{
    private TKey _key = default!;
    private Func<TKey, CancellationToken, BuildTask> _run = null!;
    private bool _started;

    internal LaunchComputation(ulong key, int occurrence) : base(key, occurrence) => Computation.Async = this;

    internal void Update(TKey key, Func<TKey, CancellationToken, BuildTask> run)
    {
        _run = run;
        if (_started && EqualityComparer<TKey>.Default.Equals(_key, key))
            return;
        _key = key;
        _started = true;
        Restart();
    }

    internal override BuildTask Run(CancellationToken cancel) => _run(_key, cancel);
}

internal sealed class ResourceComputation<TSource, T> : AsyncComputation
{
    private TSource _source = default!;
    private Func<TSource, CancellationToken, ValueTask<T>> _fetch = null!;
    private bool _started;

    internal ResourceComputation(ulong key, int occurrence) : base(key, occurrence) => Computation.Async = this;

    internal Resource<T> Resource { get; } = new();

    internal void Update(TSource source, Func<TSource, CancellationToken, ValueTask<T>> fetch)
    {
        _fetch = fetch;
        if (_started && EqualityComparer<TSource>.Default.Equals(_source, source))
            return;
        _source = source;
        _started = true;
        Restart();
    }

    internal override async BuildTask Run(CancellationToken cancel)
    {
        Resource.BeginLoad();
        try
        {
            T value = await _fetch(_source, cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            Resource.CompleteLoad(value);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested)
        {
            Resource.FailLoad(error);
        }
    }
}
