using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Instances;

[Flags]
public enum RenderInstanceChanges : byte
{
    None = 0,
    Values = 1 << 0,
    Structure = 1 << 1,
}

/// <summary>Value key for a composition that intentionally has one unclassified group.</summary>
public readonly struct RenderInstanceSingleGroup : IEquatable<RenderInstanceSingleGroup>
{
    public bool Equals(RenderInstanceSingleGroup other) => true;

    public override bool Equals(object? obj) => obj is RenderInstanceSingleGroup;

    public override int GetHashCode() => 0;
}

/// <summary>Read-only access to one pipeline system's current physical composition.</summary>
public interface IRenderInstanceBatchSource<TGroupKey>
    where TGroupKey : notnull
{
    RenderInstanceBatches<TGroupKey>? Current { get; }
}

/// <summary>
/// Pipeline/material-owned policy for composing exact-layout instance batches. The generic
/// builder understands only group equality, layouts, and restricted write slices; every material,
/// pass, geometry, and property meaning remains inside the concrete composer.
/// </summary>
public interface IRenderInstanceBatchComposer<TGroupKey>
    where TGroupKey : notnull
{
    /// <summary>Reports packet-level invalidation relative to the owning system version.</summary>
    RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion);

    /// <summary>Returns how many exact-layout groups one RenderWorld entity contributes to.</summary>
    int CountGroups(ReadOnlyQueryPacket packet, int entityRow);

    /// <summary>Returns one group key in the entity-local order used by CountGroups.</summary>
    TGroupKey GetGroup(
        ReadOnlyQueryPacket packet,
        int entityRow,
        int groupIndex);

    /// <summary>Resolves the exact property layout for one concrete group.</summary>
    RenderInstancePropertyLayout GetLayout(in TGroupKey group);

    /// <summary>Binds shared/per-instance metadata once for one allocated group.</summary>
    void Bind(in TGroupKey group, RenderInstanceWriteSlice destination);

    /// <summary>
    /// Writes one entity contribution into a one-row destination. The group index is local to the
    /// entity and lets a material/pass composer read the matching pipeline-owned membership.
    /// </summary>
    void Write(
        in TGroupKey group,
        int groupIndex,
        ReadOnlyQueryPacket packet,
        int entityRow,
        RenderInstanceWriteSlice destination);

    void WritePacket(
        in TGroupKey group,
        int groupIndex,
        ReadOnlyQueryPacket packet,
        RenderInstanceWriteSlice destination)
    {
        for (int row = 0; row < packet.Count; row++)
        {
            Write(
                in group,
                groupIndex,
                packet,
                row,
                destination.Slice(row, 1));
        }
    }
}

/// <summary>One internal physical batch selected by a concrete group key.</summary>
public readonly record struct RenderInstanceBatchGroup<TGroupKey>(
    TGroupKey Key,
    RenderInstancePropertyLayout Layout,
    RenderInstanceBatch Batch)
    where TGroupKey : notnull;

/// <summary>
/// Current-plan physical address for one entity contribution. It is not an entity identity or a
/// persistent slot: rebuilding the plan may change both Batch and Row.
/// </summary>
public readonly struct RenderInstanceBatchAddress<TGroupKey>
    where TGroupKey : notnull
{
    internal RenderInstanceBatchAddress(
        TGroupKey key,
        RenderInstanceBatch batch,
        int row,
        int groupOrdinal)
    {
        Key = key;
        Batch = batch;
        Row = row;
        GroupOrdinal = groupOrdinal;
    }

    public TGroupKey Key { get; }

    public RenderInstanceBatch Batch { get; }

    public int Row { get; }

    internal int GroupOrdinal { get; }
}

/// <summary>
/// Immutable result of one storage composition. It retains no Entity array and publishes no
/// entity-to-batch ownership table. A consumer traversing the same current query order can borrow
/// the addresses for its dense entity index while producing draw or GPU-candidate work.
/// </summary>
public sealed class RenderInstanceBatches<TGroupKey>
    where TGroupKey : notnull
{
    private readonly RenderInstanceBatchGroup<TGroupKey>[] _groups;
    private readonly int[] _entityOffsets;
    private readonly RenderInstanceBatchAddress<TGroupKey>[] _addresses;

    internal RenderInstanceBatches(
        int entityCount,
        RenderInstanceBatchGroup<TGroupKey>[] groups,
        int[] entityOffsets,
        RenderInstanceBatchAddress<TGroupKey>[] addresses)
    {
        EntityCount = entityCount;
        _groups = groups;
        _entityOffsets = entityOffsets;
        _addresses = addresses;
    }

    public int EntityCount { get; }

    public int GroupCount => _groups.Length;

    public int AddressCount => _addresses.Length;

    public ReadOnlySpan<RenderInstanceBatchGroup<TGroupKey>> Groups => _groups;

    public ReadOnlySpan<RenderInstanceBatchAddress<TGroupKey>> RowsForEntity(
        int denseEntityIndex)
    {
        if ((uint)denseEntityIndex >= (uint)EntityCount)
            throw new ArgumentOutOfRangeException(nameof(denseEntityIndex));
        int start = _entityOffsets[denseEntityIndex];
        return _addresses.AsSpan(
            start,
            _entityOffsets[denseEntityIndex + 1] - start);
    }

    internal int[] EntityOffsets => _entityOffsets;

    internal RenderInstanceBatchAddress<TGroupKey>[] Addresses => _addresses;
}

/// <summary>
/// Pipeline-owned composition root over the engine-wide instance-storage system. It performs one
/// query packetization, parallel count/fill, stable worker-local grouping, exact-layout allocation,
/// and disjoint direct-to-SoA writes. It never creates a batch Entity or writes a slot back to ECS.
/// </summary>
public sealed class RenderInstanceBatchBuilder<TGroupKey>
    where TGroupKey : notnull
{
    private const int InlinePacketRowLimit = 4_096;
    private const RenderInstanceChanges KnownChanges =
        RenderInstanceChanges.Values | RenderInstanceChanges.Structure;

    private readonly StableGrouping<TGroupKey> _grouping;
    private readonly int _rowsPerPacket;
    private readonly JobScheduleOptions _jobOptions;
    private int _updating;

    public RenderInstanceBatchBuilder(
        int groupingPartitions,
        int rowsPerPacket = 0,
        JobScheduleOptions jobOptions = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupingPartitions);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsPerPacket);
        _grouping = new StableGrouping<TGroupKey>(groupingPartitions);
        _rowsPerPacket = rowsPerPacket;
        _jobOptions = jobOptions;
    }

    public RenderInstanceBatches<TGroupKey>? Current { get; private set; }

    /// <summary>
    /// Updates the current physical plan. Structural invalidation rebuilds group membership;
    /// value-only invalidation validates the existing group sequence before rewriting in place.
    /// </summary>
    public bool Update<TComposer>(
        RenderPrepareSystemContext context,
        QueryHandle query,
        in TComposer composer,
        RenderInstanceChanges forcedChanges = RenderInstanceChanges.None)
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        if ((forcedChanges & ~KnownChanges) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(forcedChanges),
                forcedChanges,
                "Unknown render-instance change flags were supplied.");
        }
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A render-instance batch builder cannot update concurrently.");
        }

        try
        {
            var snapshot = new UpdateSnapshot<TComposer>(
                this,
                context,
                composer,
                forcedChanges);
            context.World.ExecuteReadSnapshot(
                query,
                context.LastSystemVersion,
                ref snapshot,
                static (QueryCursor cursor, ref UpdateSnapshot<TComposer> state) =>
                    state.Update(cursor));
            return snapshot.Changed;
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
        }
    }

    /// <summary>Retires every physical batch while the owning prepare system is being destroyed.</summary>
    public void Clear(RenderPrepareSystemContext context)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A render-instance batch builder cannot clear during an update.");
        }

        try
        {
            RenderInstanceBatches<TGroupKey>? current = Current;
            Current = null;
            if (current is not null)
                Retire(context, current);
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
        }
    }

    private struct UpdateSnapshot<TComposer>
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        private readonly RenderInstanceBatchBuilder<TGroupKey> _owner;
        private readonly RenderPrepareSystemContext _context;
        private readonly TComposer _composer;
        private readonly RenderInstanceChanges _forcedChanges;

        internal UpdateSnapshot(
            RenderInstanceBatchBuilder<TGroupKey> owner,
            RenderPrepareSystemContext context,
            TComposer composer,
            RenderInstanceChanges forcedChanges)
        {
            _owner = owner;
            _context = context;
            _composer = composer;
            _forcedChanges = forcedChanges;
            Changed = false;
        }

        internal bool Changed { get; private set; }

        internal void Update(QueryCursor cursor)
        {
            ReadOnlyQueryPacketPlan packets =
                ReadOnlyQueryPacketJobs.CreatePlan(cursor, _owner._rowsPerPacket);
            try
            {
                RenderInstanceBatches<TGroupKey>? current = _owner.Current;
                if (current is null)
                {
                    _owner.Rebuild(_context, ref packets, in _composer);
                    Changed = true;
                    return;
                }

                RenderInstanceChanges changes = packets.RowCount == current.EntityCount
                    ? _forcedChanges | DetectChanges(ref packets)
                    : RenderInstanceChanges.Structure;
                if (changes == RenderInstanceChanges.None)
                    return;

                bool singleGroup =
                    typeof(TGroupKey) == typeof(RenderInstanceSingleGroup);
                if ((changes & RenderInstanceChanges.Structure) != 0
                    || (!singleGroup && !ValidateCurrent(ref packets, current)))
                {
                    _owner.Rebuild(_context, ref packets, in _composer);
                }
                else if (current.GroupCount != 0)
                {
                    _owner.Rewrite(_context, ref packets, current, in _composer);
                }
                Changed = true;
            }
            finally
            {
                packets.Dispose();
            }
        }

        private RenderInstanceChanges DetectChanges(
            ref ReadOnlyQueryPacketPlan packets)
        {
            int[] result = ArrayPool<int>.Shared.Rent(1);
            try
            {
                result[0] = 0;
                var job = new DetectChangesJob<TComposer>(_composer, result);
                if (packets.RowCount <= InlinePacketRowLimit)
                {
                    _ = packets.ExecuteInline(in job);
                }
                else
                {
                    JobResourceAccess access = JobResourceAccess.Write(result, 0, 1);
                    JobResourceAccess[] accesses = [access];
                    _ = packets.ExecuteParallel(in job, accesses, _owner._jobOptions);
                }
                return (RenderInstanceChanges)result[0];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(result);
            }
        }

        private bool ValidateCurrent(
            ref ReadOnlyQueryPacketPlan packets,
            RenderInstanceBatches<TGroupKey> current)
        {
            int[] mismatch = ArrayPool<int>.Shared.Rent(1);
            try
            {
                mismatch[0] = 0;
                var job = new ValidateCurrentJob<TComposer>(
                    _composer,
                    current.EntityOffsets,
                    current.Addresses,
                    mismatch);
                if (packets.RowCount <= InlinePacketRowLimit)
                    _ = packets.ExecuteInline(in job);
                else
                {
                    JobResourceAccess[] accesses =
                    [
                        JobResourceAccess.Read(current.EntityOffsets),
                        JobResourceAccess.Read(current.Addresses),
                        JobResourceAccess.Write(mismatch, 0, 1),
                    ];
                    _ = packets.ExecuteParallel(in job, accesses, _owner._jobOptions);
                }
                return mismatch[0] == 0;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(mismatch);
            }
        }
    }

    private void Rebuild<TComposer>(
        RenderPrepareSystemContext context,
        ref ReadOnlyQueryPacketPlan packets,
        in TComposer composer)
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        if (typeof(TGroupKey) == typeof(RenderInstanceSingleGroup))
        {
            RebuildSingleGroup(context, ref packets, in composer);
            return;
        }

        int entityCount = packets.RowCount;
        int[] counts = ArrayPool<int>.Shared.Rent(Math.Max(1, entityCount));
        int[] offsets = ArrayPool<int>.Shared.Rent(checked(entityCount + 1));
        TGroupKey[]? keys = null;
        StableGroupPlacement[]? placements = null;
        int[]? groupCounts = null;
        int[]? groupStarts = null;
        int addressCount = 0;
        try
        {
            if (entityCount != 0)
            {
                var countJob = new CountGroupsJob<TComposer>(composer, counts);
                if (packets.RowCount <= InlinePacketRowLimit)
                    _ = packets.ExecuteInline(in countJob);
                else
                {
                    JobResourceAccess countAccess =
                        JobResourceAccess.Write(counts, 0, entityCount);
                    JobResourceAccess[] countAccesses = [countAccess];
                    _ = packets.ExecuteParallel(in countJob, countAccesses, _jobOptions);
                }
            }

            if (entityCount == 0)
            {
                offsets[0] = 0;
            }
            else
            {
                var prefixJob = new PrefixGroupsJob(counts, offsets, entityCount);
                if (packets.RowCount <= InlinePacketRowLimit)
                    prefixJob.Execute();
                else
                {
                    JobResourceAccess[] prefixAccesses =
                    [
                        JobResourceAccess.Read(counts, 0, entityCount),
                        JobResourceAccess.Write(offsets, 0, entityCount + 1),
                    ];
                    JobSystem.Schedule(in prefixJob, prefixAccesses, _jobOptions).Complete();
                }
            }

            addressCount = offsets[entityCount];
            keys = ArrayPool<TGroupKey>.Shared.Rent(Math.Max(1, addressCount));
            if (addressCount != 0)
            {
                var keyJob = new WriteGroupKeysJob<TComposer>(
                    composer,
                    counts,
                    offsets,
                    keys);
                if (packets.RowCount <= InlinePacketRowLimit)
                    _ = packets.ExecuteInline(in keyJob);
                else
                {
                    JobResourceAccess[] keyAccesses =
                    [
                        JobResourceAccess.Read(counts, 0, entityCount),
                        JobResourceAccess.Read(offsets, 0, entityCount + 1),
                        JobResourceAccess.Write(keys, 0, addressCount),
                    ];
                    _ = packets.ExecuteParallel(in keyJob, keyAccesses, _jobOptions);
                }
            }

            placements = ArrayPool<StableGroupPlacement>.Shared.Rent(
                Math.Max(1, addressCount));
            groupCounts = ArrayPool<int>.Shared.Rent(Math.Max(1, addressCount));
            groupStarts = ArrayPool<int>.Shared.Rent(Math.Max(1, addressCount));
            int groupCount = _grouping.Group(
                keys,
                addressCount,
                groupCounts,
                groupStarts,
                placements,
                _jobOptions);

            RenderInstanceBatches<TGroupKey> replacement = BuildReplacement(
                context,
                ref packets,
                in composer,
                entityCount,
                offsets,
                keys,
                placements,
                groupCounts,
                groupCount,
                addressCount);
            RenderInstanceBatches<TGroupKey>? previous = Current;
            Current = replacement;
            if (previous is not null)
                Retire(context, previous);
        }
        finally
        {
            if (groupStarts is not null)
                ArrayPool<int>.Shared.Return(groupStarts);
            if (groupCounts is not null)
                ArrayPool<int>.Shared.Return(groupCounts);
            if (placements is not null)
                ArrayPool<StableGroupPlacement>.Shared.Return(placements);
            if (keys is not null)
                Return(keys, addressCount);
            ArrayPool<int>.Shared.Return(offsets);
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    private void RebuildSingleGroup<TComposer>(
        RenderPrepareSystemContext context,
        ref ReadOnlyQueryPacketPlan packets,
        in TComposer composer)
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        int entityCount = packets.RowCount;
        if (entityCount == 0)
        {
            RenderInstanceBatches<TGroupKey>? previous = Current;
            Current = new RenderInstanceBatches<TGroupKey>(0, [], [0], []);
            if (previous is not null)
                Retire(context, previous);
            return;
        }

        TGroupKey key = default!;
        RenderInstancePropertyLayout layout = composer.GetLayout(in key)
            ?? throw new InvalidOperationException(
                "A render-instance composer returned a null exact layout.");
        RenderInstanceWriteHandle? handle = null;
        RenderInstanceBatch? published = null;
        RenderInstanceWriteSlice[] destinations =
            ArrayPool<RenderInstanceWriteSlice>.Shared.Rent(1);
        try
        {
            handle = context.AllocateBatch(layout, entityCount);
            RenderInstanceWriteSlice destination = handle.OpenWrite(layout);
            destinations[0] = destination;
            composer.Bind(in key, destination);

            var fill = new RewriteCurrentJob<TComposer>(
                composer,
                [],
                [],
                destinations);
            if (packets.RowCount <= InlinePacketRowLimit)
            {
                _ = packets.ExecuteInline(in fill);
            }
            else
            {
                JobResourceAccess[] accesses =
                [
                    JobResourceAccess.Read(destinations, 0, 1),
                ];
                _ = packets.ExecuteParallel(in fill, accesses, _jobOptions);
            }

            published = handle.Publish();
            handle = null;
            var groups = new RenderInstanceBatchGroup<TGroupKey>[1]
            {
                new(key, layout, published),
            };
            var offsets = new int[entityCount + 1];
            var addresses = new RenderInstanceBatchAddress<TGroupKey>[entityCount];
            for (int entity = 0; entity < entityCount; entity++)
            {
                offsets[entity] = entity;
                addresses[entity] = new RenderInstanceBatchAddress<TGroupKey>(
                    key,
                    published,
                    entity,
                    0);
            }
            offsets[entityCount] = entityCount;

            RenderInstanceBatches<TGroupKey>? previous = Current;
            Current = new RenderInstanceBatches<TGroupKey>(
                entityCount,
                groups,
                offsets,
                addresses);
            published = null;
            if (previous is not null)
                Retire(context, previous);
        }
        catch
        {
            if (published is not null)
                context.Retire(published);
            throw;
        }
        finally
        {
            handle?.Dispose();
            destinations[0] = default;
            ArrayPool<RenderInstanceWriteSlice>.Shared.Return(destinations);
        }
    }

    private RenderInstanceBatches<TGroupKey> BuildReplacement<TComposer>(
        RenderPrepareSystemContext context,
        ref ReadOnlyQueryPacketPlan packets,
        in TComposer composer,
        int entityCount,
        int[] offsets,
        TGroupKey[] keys,
        StableGroupPlacement[] placements,
        int[] groupCounts,
        int groupCount,
        int addressCount)
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        if (groupCount == 0)
        {
            int[] emptyOffsets = new int[entityCount + 1];
            offsets.AsSpan(0, entityCount + 1).CopyTo(emptyOffsets);
            return new RenderInstanceBatches<TGroupKey>(
                entityCount,
                [],
                emptyOffsets,
                []);
        }

        RenderInstanceWriteHandle?[] handles =
            ArrayPool<RenderInstanceWriteHandle?>.Shared.Rent(groupCount);
        RenderInstanceWriteSlice[] destinations =
            ArrayPool<RenderInstanceWriteSlice>.Shared.Rent(groupCount);
        RenderInstancePropertyLayout[] layouts =
            ArrayPool<RenderInstancePropertyLayout>.Shared.Rent(groupCount);
        RenderInstanceBatch[] published =
            ArrayPool<RenderInstanceBatch>.Shared.Rent(groupCount);
        int publishedCount = 0;
        try
        {
            for (int group = 0; group < groupCount; group++)
            {
                TGroupKey key = _grouping.GetKey(group);
                RenderInstancePropertyLayout layout = composer.GetLayout(in key)
                    ?? throw new InvalidOperationException(
                        "A render-instance composer returned a null exact layout.");
                layouts[group] = layout;
                RenderInstanceWriteHandle handle = context.AllocateBatch(
                    layout,
                    groupCounts[group]);
                handles[group] = handle;
                RenderInstanceWriteSlice destination = handle.OpenWrite(layout);
                destinations[group] = destination;
                composer.Bind(in key, destination);
            }

            if (addressCount != 0)
            {
                var fillJob = new FillReplacementJob<TComposer>(
                    composer,
                    offsets,
                    keys,
                    placements,
                    destinations);
                if (packets.RowCount <= InlinePacketRowLimit)
                    _ = packets.ExecuteInline(in fillJob);
                else
                {
                    JobResourceAccess[] accesses =
                    [
                        JobResourceAccess.Read(offsets, 0, entityCount + 1),
                        JobResourceAccess.Read(keys, 0, addressCount),
                        JobResourceAccess.Read(placements, 0, addressCount),
                        JobResourceAccess.Read(destinations, 0, groupCount),
                    ];
                    _ = packets.ExecuteParallel(in fillJob, accesses, _jobOptions);
                }
            }

            for (int group = 0; group < groupCount; group++)
            {
                published[group] = handles[group]!.Publish();
                handles[group] = null;
                publishedCount++;
            }

            var persistentGroups =
                new RenderInstanceBatchGroup<TGroupKey>[groupCount];
            for (int group = 0; group < groupCount; group++)
            {
                persistentGroups[group] = new RenderInstanceBatchGroup<TGroupKey>(
                    _grouping.GetKey(group),
                    layouts[group],
                    published[group]);
            }

            var persistentOffsets = new int[entityCount + 1];
            offsets.AsSpan(0, entityCount + 1).CopyTo(persistentOffsets);
            var persistentAddresses =
                new RenderInstanceBatchAddress<TGroupKey>[addressCount];
            for (int address = 0; address < addressCount; address++)
            {
                StableGroupPlacement placement = placements[address];
                persistentAddresses[address] = new RenderInstanceBatchAddress<TGroupKey>(
                    keys[address],
                    published[placement.Group],
                    placement.Row,
                    placement.Group);
            }
            return new RenderInstanceBatches<TGroupKey>(
                entityCount,
                persistentGroups,
                persistentOffsets,
                persistentAddresses);
        }
        catch (Exception failure)
        {
            List<Exception>? failures = null;
            for (int group = 0; group < publishedCount; group++)
            {
                try
                {
                    context.Retire(published[group]);
                }
                catch (Exception cleanupFailure)
                {
                    (failures ??= [failure]).Add(cleanupFailure);
                }
            }
            if (failures is not null)
                throw new AggregateException(
                    "Render-instance composition and cleanup both failed.",
                    failures);
            throw;
        }
        finally
        {
            for (int group = 0; group < groupCount; group++)
                handles[group]?.Dispose();
            handles.AsSpan(0, groupCount).Clear();
            destinations.AsSpan(0, groupCount).Clear();
            layouts.AsSpan(0, groupCount).Clear();
            published.AsSpan(0, groupCount).Clear();
            ArrayPool<RenderInstanceWriteHandle?>.Shared.Return(handles);
            ArrayPool<RenderInstanceWriteSlice>.Shared.Return(destinations);
            ArrayPool<RenderInstancePropertyLayout>.Shared.Return(layouts);
            ArrayPool<RenderInstanceBatch>.Shared.Return(published);
        }
    }

    private void Rewrite<TComposer>(
        RenderPrepareSystemContext context,
        ref ReadOnlyQueryPacketPlan packets,
        RenderInstanceBatches<TGroupKey> current,
        in TComposer composer)
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        int groupCount = current.GroupCount;
        RenderInstanceWriteHandle?[] handles =
            ArrayPool<RenderInstanceWriteHandle?>.Shared.Rent(groupCount);
        RenderInstanceWriteSlice[] destinations =
            ArrayPool<RenderInstanceWriteSlice>.Shared.Rent(groupCount);
        try
        {
            ReadOnlySpan<RenderInstanceBatchGroup<TGroupKey>> groups = current.Groups;
            for (int group = 0; group < groupCount; group++)
            {
                RenderInstanceBatchGroup<TGroupKey> currentGroup = groups[group];
                RenderInstanceWriteHandle handle = context.RewriteBatch(
                    currentGroup.Batch,
                    currentGroup.Layout);
                handles[group] = handle;
                RenderInstanceWriteSlice destination = handle.OpenWrite(currentGroup.Layout);
                destinations[group] = destination;
                TGroupKey key = currentGroup.Key;
                composer.Bind(in key, destination);
            }

            var fillJob = new RewriteCurrentJob<TComposer>(
                composer,
                current.EntityOffsets,
                current.Addresses,
                destinations);
            if (packets.RowCount <= InlinePacketRowLimit)
                _ = packets.ExecuteInline(in fillJob);
            else
            {
                JobResourceAccess[] accesses =
                [
                    JobResourceAccess.Read(current.EntityOffsets),
                    JobResourceAccess.Read(current.Addresses),
                    JobResourceAccess.Read(destinations, 0, groupCount),
                ];
                _ = packets.ExecuteParallel(in fillJob, accesses, _jobOptions);
            }

            for (int group = 0; group < groupCount; group++)
            {
                _ = handles[group]!.Publish();
                handles[group] = null;
            }
        }
        catch (Exception failure)
        {
            Current = null;
            List<Exception>? failures = null;
            try
            {
                Retire(context, current);
            }
            catch (Exception cleanupFailure)
            {
                failures = [failure, cleanupFailure];
            }
            if (failures is not null)
                throw new AggregateException(
                    "Render-instance rewrite invalidated its batches and cleanup failed.",
                    failures);
            throw;
        }
        finally
        {
            for (int group = 0; group < groupCount; group++)
                handles[group]?.Dispose();
            handles.AsSpan(0, groupCount).Clear();
            destinations.AsSpan(0, groupCount).Clear();
            ArrayPool<RenderInstanceWriteHandle?>.Shared.Return(handles);
            ArrayPool<RenderInstanceWriteSlice>.Shared.Return(destinations);
        }
    }

    private static void Retire(
        RenderPrepareSystemContext context,
        RenderInstanceBatches<TGroupKey> batches)
    {
        List<Exception>? failures = null;
        ReadOnlySpan<RenderInstanceBatchGroup<TGroupKey>> groups = batches.Groups;
        for (int group = 0; group < groups.Length; group++)
        {
            try
            {
                context.Retire(groups[group].Batch);
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
                    "Not every render-instance batch could be retired.",
                    failures);
        }
    }

    private static void Return(TGroupKey[] values, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TGroupKey>())
            values.AsSpan(0, count).Clear();
        ArrayPool<TGroupKey>.Shared.Return(values);
    }

    private readonly struct DetectChangesJob<TComposer>(
        TComposer composer,
        int[] result) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            RenderInstanceChanges changes = composer.GetChanges(
                packet,
                context.LastSystemVersion);
            if ((changes & ~KnownChanges) != 0)
            {
                throw new InvalidOperationException(
                    $"A render-instance composer returned unknown change flags '{changes}'.");
            }
            _ = Interlocked.Or(ref result[0], (int)changes);
        }
    }

    private readonly struct CountGroupsJob<TComposer>(
        TComposer composer,
        int[] counts) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TComposer local = composer;
            for (int row = 0; row < packet.Count; row++)
            {
                int count = local.CountGroups(packet, row);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "A render-instance composer returned a negative group count.");
                }
                counts[checked(context.OutputStart + row)] = count;
            }
        }
    }

    private readonly struct PrefixGroupsJob(
        int[] counts,
        int[] offsets,
        int entityCount) : IJob
    {
        public void Execute()
        {
            int total = 0;
            offsets[0] = 0;
            for (int entity = 0; entity < entityCount; entity++)
            {
                total = checked(total + counts[entity]);
                offsets[entity + 1] = total;
            }
        }
    }

    private readonly struct WriteGroupKeysJob<TComposer>(
        TComposer composer,
        int[] counts,
        int[] offsets,
        TGroupKey[] keys) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TComposer local = composer;
            for (int row = 0; row < packet.Count; row++)
            {
                int entity = checked(context.OutputStart + row);
                int count = counts[entity];
                int start = offsets[entity];
                for (int group = 0; group < count; group++)
                {
                    TGroupKey key = local.GetGroup(packet, row, group);
                    if (key is null)
                    {
                        throw new InvalidOperationException(
                            "A render-instance composer returned a null group key.");
                    }
                    keys[start + group] = key;
                }
            }
        }
    }

    private readonly struct ValidateCurrentJob<TComposer>(
        TComposer composer,
        int[] entityOffsets,
        RenderInstanceBatchAddress<TGroupKey>[] addresses,
        int[] mismatch) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            if (Volatile.Read(ref mismatch[0]) != 0)
                return;

            TComposer local = composer;
            EqualityComparer<TGroupKey> comparer = EqualityComparer<TGroupKey>.Default;
            for (int row = 0; row < packet.Count; row++)
            {
                int entity = checked(context.OutputStart + row);
                int start = entityOffsets[entity];
                int count = entityOffsets[entity + 1] - start;
                if (local.CountGroups(packet, row) != count)
                {
                    Volatile.Write(ref mismatch[0], 1);
                    return;
                }
                for (int group = 0; group < count; group++)
                {
                    if (!comparer.Equals(
                            local.GetGroup(packet, row, group),
                            addresses[start + group].Key))
                    {
                        Volatile.Write(ref mismatch[0], 1);
                        return;
                    }
                }
            }
        }
    }

    private readonly struct FillReplacementJob<TComposer>(
        TComposer composer,
        int[] offsets,
        TGroupKey[] keys,
        StableGroupPlacement[] placements,
        RenderInstanceWriteSlice[] destinations) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TComposer local = composer;
            for (int row = 0; row < packet.Count; row++)
            {
                int entity = checked(context.OutputStart + row);
                int start = offsets[entity];
                int count = offsets[entity + 1] - start;
                for (int group = 0; group < count; group++)
                {
                    int address = start + group;
                    TGroupKey key = keys[address];
                    StableGroupPlacement placement = placements[address];
                    local.Write(
                        in key,
                        group,
                        packet,
                        row,
                        destinations[placement.Group].Slice(placement.Row, 1));
                }
            }
        }
    }

    private readonly struct RewriteCurrentJob<TComposer>(
        TComposer composer,
        int[] entityOffsets,
        RenderInstanceBatchAddress<TGroupKey>[] addresses,
        RenderInstanceWriteSlice[] destinations) : IReadOnlyQueryPacketJob
        where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TComposer local = composer;
            if (typeof(TGroupKey) == typeof(RenderInstanceSingleGroup))
            {
                TGroupKey key = default!;
                local.WritePacket(
                    in key,
                    0,
                    packet,
                    destinations[0].Slice(context.OutputStart, packet.Count));
                return;
            }

            for (int row = 0; row < packet.Count; row++)
            {
                int entity = checked(context.OutputStart + row);
                int start = entityOffsets[entity];
                int count = entityOffsets[entity + 1] - start;
                for (int group = 0; group < count; group++)
                {
                    RenderInstanceBatchAddress<TGroupKey> address = addresses[start + group];
                    TGroupKey key = address.Key;
                    local.Write(
                        in key,
                        group,
                        packet,
                        row,
                        destinations[address.GroupOrdinal].Slice(address.Row, 1));
                }
            }
        }
    }
}
