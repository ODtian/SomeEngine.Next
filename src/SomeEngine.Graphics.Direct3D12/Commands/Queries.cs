using Vortice.Direct3D12;
using NativeQueryHeapType = Vortice.Direct3D12.QueryHeapType;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private readonly HandleTable<NativeQueryPool> _queryPools;

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate();

        NativeQueryPool native = CreateNativeQueryPool(desc);
        try
        {
            HandleKey key = _queryPools.Add(native);
            return new QueryPoolHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            native.Dispose();
            throw;
        }
    }

    private NativeQueryPool CreateNativeQueryPool(in QueryPoolDesc desc)
    {
        QueryHeapDescription description = new(MapQueryHeapType(desc.Type), desc.Count, 0);
        ID3D12QueryHeap heap = _native.Device.CreateQueryHeap<ID3D12QueryHeap>(description);
        NativeQueryPool native = new(heap, desc);
        ApplyObjectName(native, heap, desc.Name);
        return native;
    }

    public void DestroyQueryPool(QueryPoolHandle pool)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeQueryPool native = GetQueryPool(pool);
        RetirementPoint point = BeginRetirement(native);
        _ = _queryPools.Remove(pool.Domain, pool.Slot, pool.Generation, "query pool");
        ScheduleRetirement(native, point);
    }

    public QueryPoolMetadata GetQueryPoolMetadata(QueryPoolHandle pool)
    {
        QueryPoolDesc desc = GetQueryPool(pool).Desc;
        return new QueryPoolMetadata(desc.Type, desc.Count, desc.ResultSize);
    }

    public ulong GetTimestampFrequency(QueueType queue)
    {
        ThrowIfUnavailable();
        NativeQueue native = _native.GetQueue(queue);
        native.Queue.GetTimestampFrequency(out ulong frequency).CheckError();
        if (frequency == 0) throw new InvalidOperationException($"D3D12 queue {queue} returned a zero timestamp frequency.");
        return frequency;
    }

    public TimestampCalibration GetTimestampCalibration(QueueType queue)
    {
        ThrowIfUnavailable();
        NativeQueue native = _native.GetQueue(queue);
        native.Queue.GetTimestampFrequency(out ulong frequency).CheckError();
        native.Queue.GetClockCalibration(out ulong gpuTimestamp, out ulong cpuTimestamp).CheckError();
        if (frequency == 0) throw new InvalidOperationException($"D3D12 queue {queue} returned a zero timestamp frequency.");
        return new TimestampCalibration(queue, cpuTimestamp, gpuTimestamp, frequency);
    }

    internal NativeQueryPool GetQueryPool(QueryPoolHandle pool) =>
        _queryPools.Get(pool.Domain, pool.Slot, pool.Generation, "query pool");

    private static NativeQueryHeapType MapQueryHeapType(QueryType type) => type switch
    {
        QueryType.Timestamp => NativeQueryHeapType.Timestamp,
        QueryType.Occlusion => NativeQueryHeapType.Occlusion,
        QueryType.PipelineStatistics => NativeQueryHeapType.PipelineStatistics,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

internal sealed class NativeQueryPool : NativeLifetime
{
    private readonly object _queryGate = new();
    private readonly bool[] _active;
    private readonly bool[] _written;

    public NativeQueryPool(ID3D12QueryHeap heap, QueryPoolDesc desc)
    {
        Heap = heap;
        Desc = desc;
        _active = new bool[checked((int)desc.Count)];
        _written = new bool[checked((int)desc.Count)];
    }

    public ID3D12QueryHeap Heap { get; }
    public QueryPoolDesc Desc { get; }

    public void ValidateRange(uint firstQuery, uint queryCount)
    {
        if (queryCount == 0 || firstQuery >= Desc.Count || queryCount > Desc.Count - firstQuery)
            throw new ArgumentOutOfRangeException(nameof(firstQuery));
    }

    public void ValidateReset(uint firstQuery, uint queryCount)
    {
        ValidateRange(firstQuery, queryCount);
        lock (_queryGate)
        {
            for (uint query = firstQuery; query < firstQuery + queryCount; query++)
            {
                int index = checked((int)query);
                if (_active[index]) throw new InvalidOperationException($"Query {query} is active and cannot be reset.");
            }
        }
    }

    public void Begin(uint queryIndex)
    {
        ValidateRange(queryIndex, 1);
        lock (_queryGate)
        {
            int index = checked((int)queryIndex);
            if (_active[index]) throw new InvalidOperationException($"Query {queryIndex} is already active.");
            _active[index] = true;
        }
    }

    public void End(uint queryIndex)
    {
        ValidateRange(queryIndex, 1);
        lock (_queryGate)
        {
            int index = checked((int)queryIndex);
            if (!_active[index]) throw new InvalidOperationException($"Query {queryIndex} was not begun.");
            _active[index] = false;
        }
    }

    public void CancelBegin(uint queryIndex)
    {
        lock (_queryGate) _active[checked((int)queryIndex)] = false;
    }

    public void WriteTimestamp(uint queryIndex)
    {
        ValidateRange(queryIndex, 1);
        lock (_queryGate)
        {
            int index = checked((int)queryIndex);
            if (_active[index]) throw new InvalidOperationException($"Query {queryIndex} is active.");
        }
    }

    public bool IsWritten(uint queryIndex)
    {
        ValidateRange(queryIndex, 1);
        lock (_queryGate)
            return _written[checked((int)queryIndex)];
    }

    public void CommitAvailability(uint queryIndex, bool written)
    {
        ValidateRange(queryIndex, 1);
        lock (_queryGate) _written[checked((int)queryIndex)] = written;
    }

    protected override void DisposeNative() => Heap.Dispose();
}

internal readonly record struct QueryAvailabilityMutation(
    NativeQueryPool Pool,
    uint Index,
    bool Written);
