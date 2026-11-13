namespace SomeEngine.RenderGraph;

internal readonly record struct CommandContextPoolKey(QueueType Type, uint Index, bool Bundle);

internal sealed class CommandContextPool : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly ConcurrentDictionary<CommandContextPoolKey, ConcurrentBag<CommandContextPoolEntry>> _idle = new();
    private readonly ConcurrentBag<CommandContextPoolEntry> _all = new();
    private bool _disposed;

    internal CommandContextPool(IGraphicsBackend backend, Device device)
    {
        _backend = backend;
        _device = device;
    }

    internal CommandContextHandle Acquire(Queue queue, bool bundle, string? label = null, uint initialCommandCount = 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new CommandContextPoolKey(queue.Type, queue.Index, bundle);
        ConcurrentBag<CommandContextPoolEntry> bag =
            _idle.GetOrAdd(key, static _ => new ConcurrentBag<CommandContextPoolEntry>());
        if (!bag.TryTake(out CommandContextPoolEntry? entry))
        {
            entry = new CommandContextPoolEntry(_backend.CreateCommandContext(
                _device,
                new CommandContextDesc(
                    queue.Type,
                    queue.Index,
                    InitialBufferCount: initialCommandCount,
                    Bundle: bundle,
                    Label: label)));
            _all.Add(entry);
        }
        long useIdentity = entry.BeginUse();
        return new CommandContextHandle(this, key, entry, useIdentity);
    }

    private void Return(
        in CommandContextPoolKey key,
        CommandContextPoolEntry entry,
        long useIdentity)
    {
        if (!entry.EndUse(useIdentity)) return;
        if (_disposed) return;
        _idle.GetOrAdd(key, static _ => new ConcurrentBag<CommandContextPoolEntry>()).Add(entry);
    }

    public void Dispose()
    {
        _disposed = true;
        while (_all.TryTake(out CommandContextPoolEntry? entry))
        {
            try { entry.Context.Dispose(); }
            catch { _all.Add(entry); throw; }
        }
        _idle.Clear();
    }

    internal sealed class CommandContextPoolEntry
    {
        private long _nextUseIdentity;
        private long _activeUseIdentity;

        internal CommandContextPoolEntry(CommandContext context) => Context = context;

        internal CommandContext Context { get; }

        internal long BeginUse()
        {
            long identity = Interlocked.Increment(ref _nextUseIdentity);
            if (identity <= 0)
                throw new InvalidOperationException("The CommandContext lease identity space is exhausted.");
            if (Interlocked.CompareExchange(ref _activeUseIdentity, identity, 0) != 0)
                throw new InvalidOperationException("The CommandContext pool entry is already leased.");
            return identity;
        }

        internal bool EndUse(long identity) =>
            identity > 0 &&
            Interlocked.CompareExchange(ref _activeUseIdentity, 0, identity) == identity;
    }

    internal readonly struct CommandContextHandle : IDisposable
    {
        private readonly CommandContextPool? _pool;
        private readonly CommandContextPoolKey _key;
        private readonly CommandContextPoolEntry? _entry;
        private readonly long _identity;

        internal CommandContextHandle(
            CommandContextPool pool,
            in CommandContextPoolKey key,
            CommandContextPoolEntry entry,
            long identity)
        {
            _pool = pool;
            _key = key;
            _entry = entry;
            _identity = identity;
        }

        internal CommandContext Context => _entry?.Context ??
            throw new InvalidOperationException("The CommandContext lease is invalid.");

        public void Dispose()
        {
            if (_pool is not null && _entry is not null)
                _pool.Return(_key, _entry, _identity);
        }
    }
}

