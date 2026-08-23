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

    internal CommandContextLease Acquire(Queue queue, bool bundle, string? label = null)
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
                    InitialSlotCount: 4,
                    Bundle: bundle,
                    Label: label)));
            _all.Add(entry);
        }
        long leaseIdentity = entry.BeginLease();
        return new CommandContextLease(this, key, entry, leaseIdentity);
    }

    private void Return(
        in CommandContextPoolKey key,
        CommandContextPoolEntry entry,
        long leaseIdentity)
    {
        if (!entry.EndLease(leaseIdentity)) return;
        if (_disposed) return;
        _idle.GetOrAdd(key, static _ => new ConcurrentBag<CommandContextPoolEntry>()).Add(entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_all.TryTake(out CommandContextPoolEntry? entry)) entry.Context.Dispose();
        _idle.Clear();
    }

    internal sealed class CommandContextPoolEntry
    {
        private long _nextLeaseIdentity;
        private long _activeLeaseIdentity;

        internal CommandContextPoolEntry(CommandContext context) => Context = context;

        internal CommandContext Context { get; }

        internal long BeginLease()
        {
            long identity = Interlocked.Increment(ref _nextLeaseIdentity);
            if (identity <= 0)
                throw new InvalidOperationException("The CommandContext lease identity space is exhausted.");
            if (Interlocked.CompareExchange(ref _activeLeaseIdentity, identity, 0) != 0)
                throw new InvalidOperationException("The CommandContext pool entry is already leased.");
            return identity;
        }

        internal bool EndLease(long identity) =>
            identity > 0 &&
            Interlocked.CompareExchange(ref _activeLeaseIdentity, 0, identity) == identity;
    }

    internal readonly struct CommandContextLease : IDisposable
    {
        private readonly CommandContextPool? _owner;
        private readonly CommandContextPoolKey _key;
        private readonly CommandContextPoolEntry? _entry;
        private readonly long _identity;

        internal CommandContextLease(
            CommandContextPool owner,
            in CommandContextPoolKey key,
            CommandContextPoolEntry entry,
            long identity)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
            _identity = identity;
        }

        internal CommandContext Context => _entry?.Context ??
            throw new InvalidOperationException("The CommandContext lease is invalid.");

        public void Dispose()
        {
            if (_owner is not null && _entry is not null)
                _owner.Return(_key, _entry, _identity);
        }
    }
}

