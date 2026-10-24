using System.Runtime.InteropServices;

namespace SomeEngine.Core.Collections;

/// <summary>Shares live values and in-progress creation for each key.</summary>
/// <remarks>
/// Values are held by short weak handles. Callers keep ordinary references for as long as they need them.
/// Each new creation checks up to four existing records; the arrays retain their reusable capacity.
/// </remarks>
public sealed class WeakValues<TKey, T>
    where TKey : notnull
    where T : class
{
    private readonly object _sync = new();
    private readonly IEqualityComparer<TKey> _comparer;
    private readonly Dictionary<TKey, TaskCompletionSource<T>> _creations;
    private int[] _buckets = new int[16];
    private Record[] _records = new Record[16];
    private int _recordCount;
    private int _freeRecord = -1;
    private int _nextCheck;

    public WeakValues(IEqualityComparer<TKey>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _creations = new Dictionary<TKey, TaskCompletionSource<T>>(_comparer);
    }

    ~WeakValues()
    {
        lock (_sync)
        {
            for (int index = 0; index < _recordCount; index++)
            {
                ref Record record = ref _records[index];
                if (record.Value.IsAllocated)
                {
                    WeakGCHandle<T> value = record.Value;
                    record.Value = default;
                    value.Dispose();
                }
            }
        }
    }

    /// <summary>Gets the live value or the shared creation result for <paramref name="key" />.</summary>
    /// <param name="completion">
    /// The unique creator receives the completion source and passes it with its complete creation operation
    /// to <see cref="CompleteAsync" />. Other callers receive <see langword="null" />.
    /// </param>
    public ValueTask<T> GetOrBegin(TKey key, out TaskCompletionSource<T>? completion)
    {
        try
        {
            lock (_sync)
            {
                uint hash = (uint)_comparer.GetHashCode(key);
                int bucket = (int)(hash & (uint)(_buckets.Length - 1));
                for (int index = _buckets[bucket] - 1; index >= 0; index = _records[index].Next)
                {
                    ref Record record = ref _records[index];
                    if (record.Hash != hash || !_comparer.Equals(record.Key, key))
                        continue;

                    if (record.Value.TryGetTarget(out T? value))
                    {
                        completion = null;
                        return new ValueTask<T>(value);
                    }

                    RemoveRecord(index);
                    break;
                }

                if (_creations.TryGetValue(key, out TaskCompletionSource<T>? creation))
                {
                    completion = null;
                    return new ValueTask<T>(creation.Task);
                }

                CheckCollectedValues();
                completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _creations.Add(key, completion);
                return new ValueTask<T>(completion.Task);
            }
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    /// <summary>Publishes the complete creation result and ends its shared work.</summary>
    /// <remarks>
    /// Invoke the asset opening, reading and value creation operation outside this index, then pass that
    /// operation here. A waiting caller can cancel its own wait with <c>Task.WaitAsync</c>; the creator
    /// controls cancellation of the supplied operation.
    /// </remarks>
    public async ValueTask<T> CompleteAsync(
        TKey key,
        TaskCompletionSource<T> completion,
        ValueTask<T> creation)
    {
        try
        {
            T value = await creation.ConfigureAwait(false);
            lock (_sync)
            {
                AddRecord(key, value);
                _creations.Remove(key);
                completion.SetResult(value);
            }
        }
        catch (OperationCanceledException error)
        {
            lock (_sync)
            {
                _creations.Remove(key);
                completion.SetCanceled(error.CancellationToken);
            }
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _creations.Remove(key);
                completion.SetException(error);
            }
        }
        finally
        {
            GC.KeepAlive(this);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private void CheckCollectedValues()
    {
        int checks = System.Math.Min(4, _recordCount);
        for (int check = 0; check < checks; check++)
        {
            if (_nextCheck == _recordCount)
                _nextCheck = 0;
            int index = _nextCheck++;
            ref Record record = ref _records[index];
            if (record.Value.IsAllocated && !record.Value.TryGetTarget(out _))
                RemoveRecord(index);
        }
    }

    private void AddRecord(TKey key, T value)
    {
        int index;
        if (_freeRecord >= 0)
        {
            index = _freeRecord;
            _freeRecord = _records[index].Next;
        }
        else
        {
            if (_recordCount == _records.Length)
                Grow();
            index = _recordCount++;
        }

        uint hash = (uint)_comparer.GetHashCode(key);
        int bucket = (int)(hash & (uint)(_buckets.Length - 1));
        _records[index] = new Record
        {
            Key = key,
            Hash = hash,
            Next = _buckets[bucket] - 1,
            Value = new WeakGCHandle<T>(value, trackResurrection: false)
        };
        _buckets[bucket] = index + 1;
    }

    private void RemoveRecord(int index)
    {
        ref Record record = ref _records[index];
        int bucket = (int)(record.Hash & (uint)(_buckets.Length - 1));
        int previous = -1;
        for (int current = _buckets[bucket] - 1; current != index; current = _records[current].Next)
            previous = current;

        if (previous < 0)
            _buckets[bucket] = record.Next + 1;
        else
            _records[previous].Next = record.Next;

        WeakGCHandle<T> value = record.Value;
        record = new Record { Next = _freeRecord };
        _freeRecord = index;
        value.Dispose();
    }

    private void Grow()
    {
        int capacity = _records.Length * 2;
        var buckets = new int[capacity];
        Array.Resize(ref _records, capacity);
        for (int index = 0; index < _recordCount; index++)
        {
            ref Record record = ref _records[index];
            if (!record.Value.IsAllocated)
                continue;
            int bucket = (int)(record.Hash & (uint)(capacity - 1));
            record.Next = buckets[bucket] - 1;
            buckets[bucket] = index + 1;
        }
        _buckets = buckets;
    }

    private struct Record
    {
        public TKey Key;
        public uint Hash;
        public int Next;
        public WeakGCHandle<T> Value;
    }
}
