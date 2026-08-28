namespace SomeEngine.Render.Instances;

/// <summary>
/// Stable logical identity of one instanced-mesh resource in a collection. Physical storage rows
/// and dense collection order are intentionally not exposed through this value.
/// </summary>
public readonly struct RenderMeshInstanceHandle : IEquatable<RenderMeshInstanceHandle>
{
    internal RenderMeshInstanceHandle(int index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    internal int Index { get; }

    internal uint Generation { get; }

    public bool IsValid => Index >= 0 && Generation != 0u;

    public bool Equals(RenderMeshInstanceHandle other) =>
        Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) =>
        obj is RenderMeshInstanceHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    public static bool operator ==(
        RenderMeshInstanceHandle left,
        RenderMeshInstanceHandle right) => left.Equals(right);

    public static bool operator !=(
        RenderMeshInstanceHandle left,
        RenderMeshInstanceHandle right) => !left.Equals(right);

    public override string ToString() => IsValid
        ? $"RenderMeshInstanceHandle({Index}:{Generation})"
        : "RenderMeshInstanceHandle(Invalid)";
}

/// <summary>
/// Scene-level registry for user-owned instanced-mesh resources. It owns no renderer and imposes
/// no ECS identity. Handles remain stable until removal; slot reuse always increments generation.
/// </summary>
public sealed class RenderMeshInstanceCollection : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Stack<int> _free = [];
    private int _count;
    private ulong _revision = 1ul;
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _count;
            }
        }
    }

    public ulong Revision
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _revision;
            }
        }
    }

    public RenderMeshInstanceHandle Add(
        RenderMeshInstanceSet set,
        bool ownsSet = false)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_gate)
        {
            ThrowIfDisposed();
            int index;
            Entry entry;
            if (_free.TryPop(out index))
            {
                Entry prior = _entries[index];
                uint generation = NextGeneration(prior.Generation);
                entry = new Entry(set, ownsSet, generation);
                _entries[index] = entry;
            }
            else
            {
                index = _entries.Count;
                entry = new Entry(set, ownsSet, Generation: 1u);
                _entries.Add(entry);
            }

            _count++;
            _revision = NextRevision(_revision);
            return new RenderMeshInstanceHandle(index, entry.Generation);
        }
    }

    public bool Contains(RenderMeshInstanceHandle handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryGetEntry(handle, out _);
        }
    }

    public bool TryGet(
        RenderMeshInstanceHandle handle,
        out RenderMeshInstanceSet? set)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryGetEntry(handle, out Entry entry))
            {
                set = entry.Set;
                return true;
            }
            set = null;
            return false;
        }
    }

    public RenderMeshInstanceSet GetRequired(RenderMeshInstanceHandle handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!TryGetEntry(handle, out Entry entry))
                throw new KeyNotFoundException($"Instance set handle '{handle}' is not live.");
            return entry.Set!;
        }
    }

    public bool Remove(RenderMeshInstanceHandle handle)
    {
        RenderMeshInstanceSet? dispose = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!TryGetEntry(handle, out Entry entry))
                return false;

            if (entry.OwnsSet)
                dispose = entry.Set;
            _entries[handle.Index] = new Entry(
                Set: null,
                OwnsSet: false,
                Generation: entry.Generation);
            _free.Push(handle.Index);
            _count--;
            _revision = NextRevision(_revision);
        }
        dispose?.Dispose();
        return true;
    }

    /// <summary>
    /// Captures collection membership and one coherent revision of every live set. Mutations after
    /// this call do not alter the returned membership or set snapshots.
    /// </summary>
    public RenderMeshInstanceCollectionSnapshot Capture(
        IReadOnlyDictionary<RenderMeshInstanceHandle, ulong>? previousDataRevisions = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshots = new RenderMeshInstanceEntrySnapshot[_count];
            int destination = 0;
            try
            {
                for (int index = 0; index < _entries.Count; index++)
                {
                    Entry entry = _entries[index];
                    if (entry.Set is null)
                        continue;
                    var handle = new RenderMeshInstanceHandle(index, entry.Generation);
                    ulong previousRevision = 0ul;
                    _ = previousDataRevisions?.TryGetValue(handle, out previousRevision);
                    snapshots[destination++] = new RenderMeshInstanceEntrySnapshot(
                        handle,
                        entry.Set.Capture(previousRevision));
                }
                if (destination != snapshots.Length)
                    throw new InvalidOperationException("Instance collection membership accounting is inconsistent.");
                return new RenderMeshInstanceCollectionSnapshot(_revision, snapshots);
            }
            catch
            {
                for (int index = 0; index < destination; index++)
                    snapshots[index].Snapshot.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Captures collection membership and shared draw state without acquiring any per-instance
    /// source-data leases.
    /// </summary>
    public RenderMeshInstanceSharedCollectionSnapshot CaptureShared()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var entries = new RenderMeshInstanceSharedEntrySnapshot[_count];
            int destination = 0;
            for (int index = 0; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                if (entry.Set is null)
                    continue;
                entries[destination++] = new RenderMeshInstanceSharedEntrySnapshot(
                    new RenderMeshInstanceHandle(index, entry.Generation),
                    entry.Set.CaptureShared());
            }
            if (destination != entries.Length)
            {
                throw new InvalidOperationException(
                    "Instance collection shared-state accounting is inconsistent.");
            }
            return new RenderMeshInstanceSharedCollectionSnapshot(_revision, entries);
        }
    }

    public void Dispose()
    {
        RenderMeshInstanceSet[] owned;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            owned = [.. _entries
                .Where(static entry => entry.OwnsSet && entry.Set is not null)
                .Select(static entry => entry.Set!)];
            _entries.Clear();
            _free.Clear();
            _count = 0;
        }

        List<Exception>? failures = null;
        foreach (RenderMeshInstanceSet set in owned)
        {
            try
            {
                set.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "One or more owned instanced-mesh resources could not be disposed.",
                    failures);
        }
    }

    private bool TryGetEntry(RenderMeshInstanceHandle handle, out Entry entry)
    {
        if (handle.IsValid && (uint)handle.Index < (uint)_entries.Count)
        {
            entry = _entries[handle.Index];
            if (entry.Set is not null && entry.Generation == handle.Generation)
                return true;
        }
        entry = default;
        return false;
    }

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1u);
        return next == 0u ? 1u : next;
    }

    private static ulong NextRevision(ulong revision) => checked(revision + 1ul);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct Entry(
        RenderMeshInstanceSet? Set,
        bool OwnsSet,
        uint Generation);
}

public readonly record struct RenderMeshInstanceEntrySnapshot(
    RenderMeshInstanceHandle Handle,
    RenderMeshInstanceSnapshot Snapshot);

public readonly record struct RenderMeshInstanceSharedEntrySnapshot(
    RenderMeshInstanceHandle Handle,
    RenderMeshInstanceSharedSnapshot Snapshot);

/// <summary>Immutable membership and shared-state snapshot with no source-data leases.</summary>
public sealed class RenderMeshInstanceSharedCollectionSnapshot
{
    private readonly RenderMeshInstanceSharedEntrySnapshot[] _entries;

    internal RenderMeshInstanceSharedCollectionSnapshot(
        ulong revision,
        RenderMeshInstanceSharedEntrySnapshot[] entries)
    {
        Revision = revision;
        _entries = entries;
    }

    public ulong Revision { get; }

    public ReadOnlySpan<RenderMeshInstanceSharedEntrySnapshot> Entries => _entries;
}

/// <summary>Owned coherent snapshot of one collection revision.</summary>
public sealed class RenderMeshInstanceCollectionSnapshot : IDisposable
{
    private RenderMeshInstanceEntrySnapshot[]? _entries;

    internal RenderMeshInstanceCollectionSnapshot(
        ulong revision,
        RenderMeshInstanceEntrySnapshot[] entries)
    {
        Revision = revision;
        _entries = entries;
    }

    public ulong Revision { get; }

    public ReadOnlySpan<RenderMeshInstanceEntrySnapshot> Entries =>
        Volatile.Read(ref _entries)
        ?? throw new ObjectDisposedException(nameof(RenderMeshInstanceCollectionSnapshot));

    public void Dispose()
    {
        RenderMeshInstanceEntrySnapshot[]? entries =
            Interlocked.Exchange(ref _entries, null);
        if (entries is null)
            return;
        List<Exception>? failures = null;
        foreach (ref readonly RenderMeshInstanceEntrySnapshot entry in entries.AsSpan())
        {
            try
            {
                entry.Snapshot.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "One or more instanced-mesh snapshots could not be released.",
                    failures);
        }
    }
}
