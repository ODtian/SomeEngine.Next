using System.Runtime.CompilerServices;

namespace SomeEngine.Assets;

/// <summary>Retains rereadable asset arrays in one byte budget using integer-linked LRU records.</summary>
public sealed class AssetByteCache
{
    private struct CachedData
    {
        internal AssetByteKey Key;
        internal byte[]? Bytes;
        internal int Previous;
        internal int Next;
    }

    private readonly object _gate = new();
    private Dictionary<AssetByteKey, int> _index = [];
    private readonly Dictionary<AssetByteKey, Task<byte[]>> _reads = [];
    private CachedData[] _data = [];
    private int _first = -1;
    private int _last = -1;
    private int _free = -1;
    private long _budget;
    private long _arrayBytes;
    private long _clearRevision;

    /// <summary>The default application cache, shared by direct file opens and loaders.</summary>
    public static AssetByteCache Shared { get; } = new();

    public AssetByteCache(long budgetBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
    }

    public long BudgetBytes { get { lock (_gate) return _budget; } }
    /// <summary>Allocation size of the arrays retained by this cache, including headers and alignment.</summary>
    public long CachedBytes { get { lock (_gate) return _arrayBytes; } }
    /// <summary>Reserved array storage for the LRU records and their lookup table.</summary>
    public long IndexBytes { get { lock (_gate) return GetIndexBytes(); } }
    public int CachedItems { get { lock (_gate) return _index.Count; } }

    public void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        lock (_gate)
        {
            _budget = budgetBytes;
            while (_first >= 0 && _arrayBytes + GetIndexBytes() > _budget) RemoveFirst();
            if (_index.Count == 0) ReleaseIndex();
        }
    }

    /// <summary>Releases cache references. Existing consumers and active reads remain valid.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _clearRevision++;
            _arrayBytes = 0;
            ReleaseIndex();
            if (_reads.Count == 0) _reads.TrimExcess();
        }
    }

    internal bool TryGet(AssetByteKey key, out byte[]? bytes)
    {
        lock (_gate) return TryGetCore(key, out bytes);
    }

    internal ValueTask<ReadOnlyMemory<byte>> ReadAsync<T>(AssetByteKey key, T input,
        Func<T, ValueTask<byte[]>> readBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<byte[]> read;
        TaskCompletionSource<byte[]>? completion = null;
        long revision;
        lock (_gate)
        {
            if (TryGetCore(key, out byte[]? bytes)) return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes!);
            revision = _clearRevision;
            if (!_reads.TryGetValue(key, out read!))
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                read = completion.Task;
                _reads.Add(key, read);
            }
        }
        if (completion is not null) _ = ReadCoreAsync(key, input, readBytes, completion, revision);
        return AwaitAsync(read, cancellationToken);
    }

    private bool TryGetCore(AssetByteKey key, out byte[]? bytes)
    {
        if (!_index.TryGetValue(key, out int index)) { bytes = null; return false; }
        bytes = _data[index].Bytes!;
        if (_last != index) { Unlink(index); Append(index); }
        return true;
    }

    private static async ValueTask<ReadOnlyMemory<byte>> AwaitAsync(Task<byte[]> read, CancellationToken token)
        => token.CanBeCanceled
            ? await read.WaitAsync(token).ConfigureAwait(false)
            : await read.ConfigureAwait(false);

    private async Task ReadCoreAsync<T>(AssetByteKey key, T input, Func<T, ValueTask<byte[]>> readBytes,
        TaskCompletionSource<byte[]> completion, long revision)
    {
        try
        {
            byte[] bytes = await readBytes(input).ConfigureAwait(false);
            lock (_gate)
            {
                if (revision == _clearRevision) Add(key, bytes);
                _reads.Remove(key);
            }
            completion.SetResult(bytes);
        }
        catch (Exception error)
        {
            lock (_gate) _reads.Remove(key);
            completion.SetException(error);
        }
    }

    private void Add(AssetByteKey key, byte[] bytes)
    {
        long charge = ArrayBytes(bytes.Length, 1);
        if (charge > _budget) return;
        while (_first >= 0 && _arrayBytes + charge + GetIndexBytes() > _budget) RemoveFirst();
        if (_arrayBytes + charge + GetIndexBytes() > _budget)
        {
            ReleaseIndex();
            if (charge + GrowthBytes(1) > _budget) return;
        }

        if (_free < 0)
        {
            int capacity = _data.Length == 0 ? 1 : checked(_data.Length * 2);
            if (_arrayBytes + charge + GrowthBytes(capacity) <= _budget) Grow(capacity);
            else if (_first >= 0) RemoveFirst();
            else return;
        }

        int index = _free;
        _free = _data[index].Next;
        _data[index].Key = key;
        _data[index].Bytes = bytes;
        _index.Add(key, index);
        Append(index);
        _arrayBytes += charge;
    }

    private void Grow(int capacity)
    {
        int previous = _data.Length;
        _index.EnsureCapacity(capacity);
        Array.Resize(ref _data, capacity);
        for (int index = capacity - 1; index >= previous; index--)
        {
            _data[index].Next = _free;
            _free = index;
        }
    }

    private void Unlink(int index)
    {
        int before = _data[index].Previous;
        int after = _data[index].Next;
        if (before < 0) _first = after; else _data[before].Next = after;
        if (after < 0) _last = before; else _data[after].Previous = before;
    }

    private void Append(int index)
    {
        _data[index].Previous = _last;
        _data[index].Next = -1;
        if (_last < 0) _first = index; else _data[_last].Next = index;
        _last = index;
    }

    private void RemoveFirst()
    {
        int index = _first;
        byte[] bytes = _data[index].Bytes!;
        Unlink(index);
        _index.Remove(_data[index].Key);
        _arrayBytes -= ArrayBytes(bytes.Length, 1);
        _data[index] = default;
        _data[index].Next = _free;
        _free = index;
    }

    private void ReleaseIndex()
    {
        _index = [];
        _data = [];
        _first = _last = _free = -1;
    }

    private long GetIndexBytes()
    {
        int capacity = _index.EnsureCapacity(0);
        return (_data.Length == 0 ? 0 : ArrayBytes(_data.Length, Unsafe.SizeOf<CachedData>()))
            + (capacity == 0 ? 0 : ArrayBytes(capacity, sizeof(int)) + ArrayBytes(capacity, LookupEntryBytes));
    }

    // Dictionary entries contain a hash, a next integer, the key, and an integer record index.
    private static int LookupEntryBytes => Align(Unsafe.SizeOf<AssetByteKey>() + 3 * sizeof(int));
    private static long GrowthBytes(int capacity)
    {
        // A conservative bound for Dictionary.EnsureCapacity's prime-sized backing arrays.
        int lookupCapacity = Math.Max(3, checked(capacity * 3));
        return ArrayBytes(capacity, Unsafe.SizeOf<CachedData>())
            + ArrayBytes(lookupCapacity, sizeof(int)) + ArrayBytes(lookupCapacity, LookupEntryBytes);
    }
    private static int Align(int bytes) => (bytes + IntPtr.Size - 1) & -IntPtr.Size;
    private static long ArrayBytes(int count, int elementBytes)
        => (3L * IntPtr.Size + (long)count * elementBytes + IntPtr.Size - 1) & -IntPtr.Size;
}

internal readonly record struct AssetByteKey(Guid Source, ulong ChunkKey, long Offset, int Length);
