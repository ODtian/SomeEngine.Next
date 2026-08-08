using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Dense output coordinates for one contiguous set of rows selected by a query. The output range
/// is a checked prefix sum over all selected rows; it is unrelated to an entity identity or a
/// persistent ECS storage position.
/// </summary>
public readonly struct ReadOnlyQueryPacketContext
{
    internal ReadOnlyQueryPacketContext(
        int packetIndex,
        int outputStart,
        int rowCount,
        uint lastSystemVersion)
    {
        PacketIndex = packetIndex;
        OutputStart = outputStart;
        RowCount = rowCount;
        LastSystemVersion = lastSystemVersion;
    }

    public int PacketIndex { get; }

    public int OutputStart { get; }

    public int RowCount { get; }

    public uint LastSystemVersion { get; }
}

/// <summary>
/// Callback-scoped, read-only borrow of one contiguous query row range. Component columns remain
/// in ECS storage and are sliced in place; filtered queries are represented as multiple contiguous
/// packets instead of gathering their rows into a temporary array.
/// </summary>
public readonly ref struct ReadOnlyQueryPacket
{
    private readonly QueryArchetypeMatch _match;
    private readonly Chunk _chunk;
    private readonly int _rowStart;

    internal ReadOnlyQueryPacket(
        QueryArchetypeMatch match,
        Chunk chunk,
        int rowStart,
        int rowCount)
    {
        _match = match;
        _chunk = chunk;
        _rowStart = rowStart;
        Count = rowCount;
    }

    public int Count { get; }

    public ReadOnlySpan<Entity> Entities =>
        _chunk.Entities.Slice(_rowStart, Count);

    public bool Has<T>() where T : struct =>
        _match.Archetype.HasComponent(Registry.ComponentMetadata<T>.Id);

    public bool HasBuffer<T>() where T : struct, IBufferElement =>
        _match.Archetype.HasComponent(BufferComponents.Header<T>())
        && _match.Archetype.HasComponent(BufferComponents.Inline<T>());

    /// <summary>
    /// Narrows this borrow to a contiguous local row range. It creates no row identity or
    /// gathered storage; component reads remain slices of the original ECS columns.
    /// </summary>
    public ReadOnlyQueryPacket Slice(int start, int count)
    {
        if ((uint)start > (uint)Count || (uint)count > (uint)(Count - start))
            throw new ArgumentOutOfRangeException(nameof(start));
        return new ReadOnlyQueryPacket(
            _match,
            _chunk,
            checked(_rowStart + start),
            count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Read<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(
            _match,
            read: true,
            write: false);
        return _chunk.ComponentRows<T>(column).Slice(_rowStart, Count);
    }

    public bool TryRead<T>(out ReadOnlySpan<T> values) where T : struct
    {
        if (!Has<T>())
        {
            values = default;
            return false;
        }

        values = Read<T>();
        return true;
    }

    /// <summary>
    /// Tests the packet's coarse component-column version against one system version. The query
    /// must declare read access to the component. This is an invalidation test, not a row identity:
    /// one changed row deliberately invalidates the packet's borrowed column.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ChangedSince<T>(uint lastSystemVersion) where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(
            _match,
            read: true,
            write: false);
        return unchecked((int)(_chunk.ChangeVersions[column] - lastSystemVersion)) > 0;
    }

    /// <summary>Tests the dynamic-buffer header column against one system version.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool BufferChangedSince<T>(uint lastSystemVersion)
        where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out _);
        return unchecked((int)(_chunk.ChangeVersions[headerColumn] - lastSystemVersion)) > 0;
    }

    public BufferView<T> ReadBuffer<T>(int row)
        where T : struct, IBufferElement
    {
        if ((uint)row >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(row));

        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out int inlineColumn);
        return new BufferView<T>(
            _chunk,
            checked(_rowStart + row),
            headerColumn,
            inlineColumn);
    }
}

/// <summary>
/// Runs once for every contiguous query packet. Implementations may write only to disjoint output
/// ranges derived from <see cref="ReadOnlyQueryPacketContext.OutputStart"/>.
/// </summary>
public interface IReadOnlyQueryPacketJob
{
    void Execute(
        in ReadOnlyQueryPacketContext context,
        ReadOnlyQueryPacket packet);
}

/// <summary>
/// Records structural commands from one contiguous read-only query packet. Every packet owns a
/// producer-private command segment, so callbacks run in parallel while playback remains stable.
/// </summary>
public interface IReadOnlyQueryPacketCommandJob
{
    void Execute(
        in ReadOnlyQueryPacketContext context,
        ReadOnlyQueryPacket packet,
        ref JobCommandWriter commands);
}

internal readonly record struct ReadOnlyPacketRange(
    QueryArchetypeMatch Match,
    Chunk Chunk,
    int RowStart,
    int RowCount,
    int OutputStart);

/// <summary>
/// Callback-scoped packetization of one query cursor. Creating the plan scans the query once;
/// several count/fill/scatter jobs may then reuse the same chunk ranges without rebuilding a
/// second entity list. The plan must be disposed before its owning query callback returns.
/// </summary>
public ref struct ReadOnlyQueryPacketPlan
{
    private readonly SomeEngine.ECS.World _owner;
    private ReadOnlyPacketRange[]? _packets;
    private bool _disposed;

    internal ReadOnlyQueryPacketPlan(
        SomeEngine.ECS.World owner,
        ReadOnlyPacketRange[]? ownedPackets,
        int packetCount,
        int rowCount,
        uint lastSystemVersion)
    {
        _owner = owner;
        _packets = ownedPackets;
        _disposed = false;
        PacketCount = packetCount;
        RowCount = rowCount;
        LastSystemVersion = lastSystemVersion;
    }

    public readonly int PacketCount { get; }

    public readonly int RowCount { get; }

    public readonly uint LastSystemVersion { get; }

    public int ExecuteParallel<TJob>(
        in TJob job,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketJob =>
        ExecuteParallel(
            in job,
            ReadOnlySpan<JobResourceAccess>.Empty,
            options);

    public int ExecuteParallel<TJob>(
        in TJob job,
        scoped ReadOnlySpan<JobResourceAccess> externalAccesses,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketJob
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ReadOnlyQueryPacketPlan));
        if (PacketCount == 0)
            return 0;
        ReadOnlyPacketRange[] packets = _packets
            ?? throw new InvalidOperationException("A non-empty packet plan lost its packet storage.");
        return ReadOnlyQueryPacketJobs.ExecutePrepared(
            packets.AsMemory(0, PacketCount),
            PacketCount,
            RowCount,
            LastSystemVersion,
            in job,
            externalAccesses,
            options);
    }

    public int ExecuteInline<TJob>(in TJob job)
        where TJob : struct, IReadOnlyQueryPacketJob
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ReadOnlyQueryPacketPlan));
        if (PacketCount == 0)
            return 0;
        ReadOnlyPacketRange[] packets = _packets
            ?? throw new InvalidOperationException("A non-empty packet plan lost its packet storage.");
        TJob local = job;
        for (int packetIndex = 0; packetIndex < PacketCount; packetIndex++)
        {
            ReadOnlyPacketRange range = packets[packetIndex];
            local.Execute(
                new ReadOnlyQueryPacketContext(
                    packetIndex,
                    range.OutputStart,
                    range.RowCount,
                    LastSystemVersion),
                new ReadOnlyQueryPacket(
                    range.Match,
                    range.Chunk,
                    range.RowStart,
                    range.RowCount));
        }
        return PacketCount;
    }

    /// <summary>
    /// Records one stable command segment per prepared packet without scanning or packetizing the
    /// query again. The returned command buffer owns all copied payloads; this plan still has to
    /// be disposed before its query callback returns.
    /// </summary>
    public JobCommandBuffer RecordParallel<TJob>(
        in TJob job,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketCommandJob =>
        RecordParallel(
            in job,
            ReadOnlySpan<JobResourceAccess>.Empty,
            options);

    public JobCommandBuffer RecordParallel<TJob>(
        in TJob job,
        scoped ReadOnlySpan<JobResourceAccess> externalAccesses,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketCommandJob
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ReadOnlyQueryPacketPlan));
        return ReadOnlyQueryPacketJobs.RecordPrepared(
            _owner,
            _packets is null
                ? ReadOnlyMemory<ReadOnlyPacketRange>.Empty
                : _packets.AsMemory(0, PacketCount),
            PacketCount,
            LastSystemVersion,
            in job,
            externalAccesses,
            options);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReadOnlyPacketRange[]? packets = _packets;
        _packets = null;
        if (packets is null)
            return;
        packets.AsSpan().Clear();
        ArrayPool<ReadOnlyPacketRange>.Shared.Return(packets);
    }
}

/// <summary>
/// Synchronous bridge from a runtime-owned query cursor to the Job scheduler. The caller must use
/// it inside the query callback that owns <paramref name="cursor"/>; all packet work completes
/// before the method returns, so neither ECS borrows nor packet descriptors can escape.
/// </summary>
public static class ReadOnlyQueryPacketJobs
{
    private const int InitialPacketCapacity = 16;

    public static int ExecuteParallel<TJob>(
        QueryCursor cursor,
        in TJob job,
        int rowsPerPacket = 0,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketJob =>
        ExecuteParallel(
            cursor,
            in job,
            ReadOnlySpan<JobResourceAccess>.Empty,
            rowsPerPacket,
            options);

    /// <summary>
    /// Executes packets with explicit Job-resource declarations for caller-owned output. ECS input
    /// ownership is already held by the surrounding query callback; the extra accesses describe
    /// only output or other external resources touched by <typeparamref name="TJob"/>.
    /// </summary>
    public static int ExecuteParallel<TJob>(
        QueryCursor cursor,
        in TJob job,
        ReadOnlySpan<JobResourceAccess> externalAccesses,
        int rowsPerPacket = 0,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketJob
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));
        using ReadOnlyQueryPacketPlan plan = CreatePlan(cursor, rowsPerPacket);
        return plan.ExecuteParallel(in job, externalAccesses, options);
    }

    /// <summary>
    /// Packetizes the cursor once for several synchronous packet-job passes. The returned plan
    /// borrows the cursor's chunks and therefore cannot outlive the current query callback.
    /// </summary>
    public static ReadOnlyQueryPacketPlan CreatePlan(
        QueryCursor cursor,
        int rowsPerPacket = 0)
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));

        ReadOnlyPacketRange[]? packets = null;
        int packetCount = 0;
        int outputCount = 0;
        try
        {
            BuildPackets(
                cursor,
                rowsPerPacket,
                ref packets,
                ref packetCount,
                ref outputCount);
            return new ReadOnlyQueryPacketPlan(
                cursor.Owner,
                packets,
                packetCount,
                outputCount,
                cursor.LastSystemVersion);
        }
        catch
        {
            if (packets is not null)
            {
                packets.AsSpan().Clear();
                ArrayPool<ReadOnlyPacketRange>.Shared.Return(packets);
            }
            throw;
        }
    }

    internal static int ExecutePrepared<TJob>(
        ReadOnlyMemory<ReadOnlyPacketRange> packets,
        int packetCount,
        int outputCount,
        uint lastSystemVersion,
        in TJob job,
        scoped ReadOnlySpan<JobResourceAccess> externalAccesses,
        JobScheduleOptions options)
        where TJob : struct, IReadOnlyQueryPacketJob
    {
        if (packetCount == 0)
            return 0;

        JobResourceAccess[]? accesses = null;
        try
        {
            int accessCount = checked(externalAccesses.Length + 1);
            accesses = ArrayPool<JobResourceAccess>.Shared.Rent(accessCount);
            accesses[0] = JobResourceAccess.Read(packets);
            externalAccesses.CopyTo(accesses.AsSpan(1, externalAccesses.Length));

            var adapter = new PacketJob<TJob>(
                packets,
                lastSystemVersion,
                job);
            JobSystem.ScheduleParallel(
                adapter,
                packetCount,
                batchSize: 1,
                accesses.AsSpan(0, accessCount),
                options).Complete();
        }
        finally
        {
            if (accesses is not null)
            {
                accesses.AsSpan().Clear();
                ArrayPool<JobResourceAccess>.Shared.Return(accesses);
            }
        }
        return outputCount;
    }

    /// <summary>
    /// Records one stable command segment per contiguous query packet. Recording completes while
    /// the caller's read snapshot is still active; returned commands own every copied payload and
    /// may be played back only after that snapshot callback returns.
    /// </summary>
    public static JobCommandBuffer RecordParallel<TJob>(
        QueryCursor cursor,
        in TJob job,
        ReadOnlySpan<JobResourceAccess> externalAccesses,
        int rowsPerPacket = 0,
        JobScheduleOptions options = default)
        where TJob : struct, IReadOnlyQueryPacketCommandJob
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));
        using ReadOnlyQueryPacketPlan plan = CreatePlan(cursor, rowsPerPacket);
        return plan.RecordParallel(in job, externalAccesses, options);
    }

    internal static JobCommandBuffer RecordPrepared<TJob>(
        SomeEngine.ECS.World owner,
        ReadOnlyMemory<ReadOnlyPacketRange> packets,
        int packetCount,
        uint lastSystemVersion,
        in TJob job,
        scoped ReadOnlySpan<JobResourceAccess> externalAccesses,
        JobScheduleOptions options)
        where TJob : struct, IReadOnlyQueryPacketCommandJob
    {
        var commands = new JobCommandBuffer(owner, packetCount);
        try
        {
            if (packetCount == 0)
                return commands;
            if (packets.Length != packetCount)
            {
                throw new InvalidOperationException(
                    "A non-empty packet plan lost its packet storage.");
            }

            JobResourceAccess[]? accesses = null;
            try
            {
                int accessCount = checked(externalAccesses.Length + 1);
                accesses = ArrayPool<JobResourceAccess>.Shared.Rent(accessCount);
                accesses[0] = JobResourceAccess.Read(packets);
                externalAccesses.CopyTo(accesses.AsSpan(1, externalAccesses.Length));
                var producer = new PacketCommandProducer<TJob>(
                    packets,
                    lastSystemVersion,
                    job);
                commands.ScheduleParallel(
                    in producer,
                    batchSize: 1,
                    accesses.AsSpan(0, accessCount),
                    options).Complete();
            }
            finally
            {
                if (accesses is not null)
                {
                    accesses.AsSpan().Clear();
                    ArrayPool<JobResourceAccess>.Shared.Return(accesses);
                }
            }

            return commands;
        }
        catch
        {
            commands.Dispose();
            throw;
        }
    }

    private static void BuildPackets(
        QueryCursor cursor,
        int rowsPerPacket,
        ref ReadOnlyPacketRange[]? packets,
        ref int packetCount,
        ref int outputCount)
    {
        QueryChunkEnumerator<NoSharedFilter> chunks = cursor.Chunks;
        while (chunks.MoveNext())
        {
            QueryArchetypeMatch match = chunks.CurrentMatch;
            Chunk chunk = chunks.CurrentChunk;
            if (!match.HasRowFilter)
            {
                AddRange(
                    match,
                    chunk,
                    rowStart: 0,
                    chunk.Count,
                    rowsPerPacket,
                    ref packets,
                    ref packetCount,
                    ref outputCount);
                continue;
            }

            ChunkRowIndexEnumerator rows = chunks.Current.RowIndices;
            int runStart = -1;
            int previous = -2;
            while (rows.MoveNext())
            {
                int row = rows.Current;
                if (row != previous + 1)
                {
                    FlushRun(
                        match,
                        chunk,
                        runStart,
                        previous,
                        rowsPerPacket,
                        ref packets,
                        ref packetCount,
                        ref outputCount);
                    runStart = row;
                }
                previous = row;
            }
            FlushRun(
                match,
                chunk,
                runStart,
                previous,
                rowsPerPacket,
                ref packets,
                ref packetCount,
                ref outputCount);
        }
    }

    private static void FlushRun(
        QueryArchetypeMatch match,
        Chunk chunk,
        int runStart,
        int runEnd,
        int rowsPerPacket,
        ref ReadOnlyPacketRange[]? packets,
        ref int packetCount,
        ref int outputCount)
    {
        if (runStart < 0)
            return;
        AddRange(
            match,
            chunk,
            runStart,
            checked(runEnd - runStart + 1),
            rowsPerPacket,
            ref packets,
            ref packetCount,
            ref outputCount);
    }

    private static void AddRange(
        QueryArchetypeMatch match,
        Chunk chunk,
        int rowStart,
        int rowCount,
        int rowsPerPacket,
        ref ReadOnlyPacketRange[]? packets,
        ref int packetCount,
        ref int outputCount)
    {
        int packetRows = rowsPerPacket == 0 ? rowCount : rowsPerPacket;
        for (int start = rowStart; start < rowStart + rowCount; start += packetRows)
        {
            int count = Math.Min(packetRows, rowStart + rowCount - start);
            EnsureCapacity(ref packets, packetCount + 1, packetCount);
            packets![packetCount++] = new ReadOnlyPacketRange(
                match,
                chunk,
                start,
                count,
                outputCount);
            outputCount = checked(outputCount + count);
        }
    }

    private static void EnsureCapacity(
        ref ReadOnlyPacketRange[]? packets,
        int required,
        int count)
    {
        if (packets is not null && packets.Length >= required)
            return;

        int capacity = packets is null
            ? Math.Max(InitialPacketCapacity, required)
            : Math.Max(checked(packets.Length * 2), required);
        ReadOnlyPacketRange[] replacement =
            ArrayPool<ReadOnlyPacketRange>.Shared.Rent(capacity);
        if (packets is not null)
        {
            packets.AsSpan(0, count).CopyTo(replacement);
            packets.AsSpan().Clear();
            ArrayPool<ReadOnlyPacketRange>.Shared.Return(packets);
        }
        packets = replacement;
    }

    private readonly struct PacketJob<TJob> : IJobParallelFor
        where TJob : struct, IReadOnlyQueryPacketJob
    {
        private readonly ReadOnlyMemory<ReadOnlyPacketRange> _packets;
        private readonly uint _lastSystemVersion;
        private readonly TJob _job;

        internal PacketJob(
            ReadOnlyMemory<ReadOnlyPacketRange> packets,
            uint lastSystemVersion,
            in TJob job)
        {
            _packets = packets;
            _lastSystemVersion = lastSystemVersion;
            _job = job;
        }

        public void Execute(int index)
        {
            JobSystem.RequireCurrentAccess(
                JobResourceAccess.Read(_packets, index, 1));
            ReadOnlyPacketRange range = _packets.Span[index];
            var context = new ReadOnlyQueryPacketContext(
                index,
                range.OutputStart,
                range.RowCount,
                _lastSystemVersion);
            var packet = new ReadOnlyQueryPacket(
                range.Match,
                range.Chunk,
                range.RowStart,
                range.RowCount);
            TJob job = _job;
            job.Execute(in context, packet);
        }
    }

    private readonly struct PacketCommandProducer<TJob>(
        ReadOnlyMemory<ReadOnlyPacketRange> packets,
        uint lastSystemVersion,
        TJob job) : IJobParallelCommandProducer
        where TJob : struct, IReadOnlyQueryPacketCommandJob
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            JobSystem.RequireCurrentAccess(
                JobResourceAccess.Read(packets, producerIndex, 1));
            ReadOnlyPacketRange range = packets.Span[producerIndex];
            var context = new ReadOnlyQueryPacketContext(
                producerIndex,
                range.OutputStart,
                range.RowCount,
                lastSystemVersion);
            var packet = new ReadOnlyQueryPacket(
                range.Match,
                range.Chunk,
                range.RowStart,
                range.RowCount);
            TJob local = job;
            local.Execute(in context, packet, ref commands);
        }
    }
}
