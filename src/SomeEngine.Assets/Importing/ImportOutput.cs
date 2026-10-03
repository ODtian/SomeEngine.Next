namespace SomeEngine.Assets.Importing;

/// <summary>Holds one conversion's new blocks until that conversion finishes.</summary>
public sealed class ImportOutput
{
    private readonly object _sync = new();
    private readonly HashSet<Guid> _declared = [];
    private readonly Dictionary<Guid, (AssetBlock Block, bool Persistent)> _blocks = [];
    private bool _finished;

    internal ImportOutput(Asset target, IEnumerable<Guid> declared)
    {
        Target = target;
        foreach (Guid id in declared)
        {
            if (!_declared.Add(id)) throw new InvalidOperationException($"Block {id} was declared twice.");
        }
    }

    public Asset Target { get; }

    /// <summary>A successful call transfers the supplied block; a rejected block stays with its caller.</summary>
    public void SetBlock<T>(T block) where T : AssetBlock, IAssetBlock<T>
    {
        ArgumentNullException.ThrowIfNull(block);
        lock (_sync)
        {
            if (_finished) throw new InvalidOperationException("This conversion has already finished.");
            if (!_declared.Contains(T.BlockTypeId)) throw new InvalidOperationException($"Block {T.BlockTypeId} was not declared by this importer.");
            if (_blocks.ContainsKey(T.BlockTypeId)) throw new InvalidOperationException($"Block {T.BlockTypeId} was produced twice.");
            _blocks[T.BlockTypeId] = (block, T.Persistent);
        }
    }

    internal void Finish() { lock (_sync) _finished = true; }
    internal IReadOnlyCollection<(AssetBlock Block, bool Persistent)> Blocks => _blocks.Values.ToArray();
    internal void Transfer() { lock (_sync) _blocks.Clear(); }

    internal async ValueTask ReleaseAsync()
    {
        AssetBlock[] blocks;
        lock (_sync)
        {
            _finished = true;
            blocks = _blocks.Values.Select(static value => value.Block).ToArray();
            _blocks.Clear();
        }
        List<Exception> errors = [];
        foreach (AssetBlock block in blocks)
            try { await block.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Imported block cleanup failed.", errors);
    }
}
