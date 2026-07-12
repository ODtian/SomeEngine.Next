namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    private const ulong NullTimestampFrequency = 1_000_000;

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
    {
        EnsureCoordinatorThread();
        desc.Validate();
        lock (_gate)
        {
            EnsureNotDisposed();
            byte[][] values = new byte[checked((int)desc.Count)][];
            for (int index = 0; index < values.Length; index++)
                values[index] = new byte[checked((int)desc.ResultSize)];
            (uint slot, uint generation) = _queryPools.Allocate(new QueryPoolRecord
            {
                Desc = desc,
                Values = values,
                Ready = new bool[values.Length],
            });
            return new QueryPoolHandle(_domain, slot, generation);
        }
    }

    public void DestroyQueryPool(QueryPoolHandle pool)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            _queryPools.Destroy(pool.Domain, pool.Slot, pool.Generation);
        }
    }

    public QueryPoolMetadata GetQueryPoolMetadata(QueryPoolHandle pool)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            QueryPoolDesc desc = RequireQueryPool(pool).Desc;
            return new QueryPoolMetadata(desc.Type, desc.Count, desc.ResultSize);
        }
    }

    public ulong GetTimestampFrequency(QueueType queue)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            return NullTimestampFrequency;
        }
    }

    public TimestampCalibration GetTimestampCalibration(QueueType queue)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            RequireSupportedQueue(queue);
            return new TimestampCalibration(queue, _timestampCounter, _timestampCounter, NullTimestampFrequency);
        }
    }

    internal void ValidateIndirectForRecording(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        uint argumentSize,
        BufferHandle countBuffer,
        ulong countBufferOffset)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            ulong argumentBytes = ValidateIndirectArguments(
                argumentBuffer,
                argumentOffset,
                maxCommandCount,
                commandStride,
                argumentSize);
            ValidateIndirectCountBuffer(
                argumentBuffer,
                argumentOffset,
                argumentBytes,
                countBuffer,
                countBufferOffset);
        }
    }

    private ulong ValidateIndirectArguments(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        uint argumentSize)
    {
        if (maxCommandCount == 0) throw new ArgumentOutOfRangeException(nameof(maxCommandCount));
        if (commandStride < argumentSize || (commandStride & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(commandStride));
        if ((argumentOffset & 3) != 0) throw new ArgumentOutOfRangeException(nameof(argumentOffset));

        BufferRecord arguments = RequireBuffer(argumentBuffer);
        if (!arguments.Desc.Usage.HasFlag(BufferUsage.Indirect))
            throw ValidationError("The indirect argument buffer lacks BufferUsage.Indirect.");
        ulong argumentBytes = checked((ulong)(maxCommandCount - 1) * commandStride + argumentSize);
        ValidateByteRange(arguments.Desc.Size, argumentOffset, argumentBytes);
        return argumentBytes;
    }

    private void ValidateIndirectCountBuffer(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        ulong argumentBytes,
        BufferHandle countBuffer,
        ulong countBufferOffset)
    {
        if (countBuffer == default)
        {
            if (countBufferOffset != 0)
                throw new ArgumentException("A count-buffer offset requires a count buffer.", nameof(countBufferOffset));
            return;
        }

        if ((countBufferOffset & 3) != 0) throw new ArgumentOutOfRangeException(nameof(countBufferOffset));
        BufferRecord counts = RequireBuffer(countBuffer);
        if (!counts.Desc.Usage.HasFlag(BufferUsage.Indirect))
            throw ValidationError("The indirect count buffer lacks BufferUsage.Indirect.");
        ValidateByteRange(counts.Desc.Size, countBufferOffset, sizeof(uint));
        ValidateIndirectRangesDoNotOverlap(
            argumentBuffer,
            argumentOffset,
            argumentBytes,
            countBuffer,
            countBufferOffset);
    }

    private static void ValidateIndirectRangesDoNotOverlap(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        ulong argumentBytes,
        BufferHandle countBuffer,
        ulong countBufferOffset)
    {
        if (countBuffer != argumentBuffer) return;
        ulong argumentEnd = checked(argumentOffset + argumentBytes);
        ulong countEnd = checked(countBufferOffset + sizeof(uint));
        if (argumentOffset < countEnd && countBufferOffset < argumentEnd)
        {
            throw new ArgumentException(
                "Indirect argument and count ranges in the same buffer must not overlap.",
                nameof(countBufferOffset));
        }
    }

    internal QueryType GetQueryTypeForRecording(QueryPoolHandle pool, uint queryIndex)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            QueryPoolRecord record = RequireQueryPool(pool);
            if (queryIndex >= record.Desc.Count) throw new ArgumentOutOfRangeException(nameof(queryIndex));
            return record.Desc.Type;
        }
    }

    internal void ValidateQueryRangeForRecording(QueryPoolHandle pool, uint firstQuery, uint queryCount)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            ValidateQueryRange(RequireQueryPool(pool), firstQuery, queryCount);
        }
    }

    internal ulong ValidateQueryResolveForRecording(
        QueryPoolHandle pool,
        uint firstQuery,
        uint queryCount,
        BufferHandle destination,
        ulong destinationOffset,
        ulong destinationStride)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            QueryPoolRecord queryPool = RequireQueryPool(pool);
            ValidateQueryRange(queryPool, firstQuery, queryCount);
            ulong resultSize = queryPool.Desc.ResultSize;
            ulong stride = destinationStride == 0 ? resultSize : destinationStride;
            if (stride < resultSize || (stride & 7) != 0)
                throw new ArgumentOutOfRangeException(nameof(destinationStride));
            if ((destinationOffset & 7) != 0) throw new ArgumentOutOfRangeException(nameof(destinationOffset));
            BufferRecord buffer = RequireBuffer(destination);
            if (!buffer.Desc.Usage.HasFlag(BufferUsage.CopyDestination))
                throw ValidationError("A query resolve destination requires BufferUsage.CopyDestination.");
            ulong required = checked((ulong)(queryCount - 1) * stride + resultSize);
            ValidateByteRange(buffer.Desc.Size, destinationOffset, required);
            return stride;
        }
    }

    private static void ValidateQueryRange(QueryPoolRecord pool, uint firstQuery, uint queryCount)
    {
        if (queryCount == 0 || firstQuery >= pool.Desc.Count || queryCount > pool.Desc.Count - firstQuery)
            throw new ArgumentOutOfRangeException(nameof(firstQuery));
    }

    private QueryPoolRecord RequireQueryPool(QueryPoolHandle handle) =>
        _queryPools.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
}
