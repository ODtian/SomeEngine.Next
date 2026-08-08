using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Buffers
{
    private Entities _entities = null!;
    private Components _components = null!;
    private Bundles _bundles = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;

    internal void Bind(
        Entities entities,
        Components components,
        Bundles bundles,
        Clock clock,
        Iteration iteration)
    {
        _entities = entities;
        _components = components;
        _bundles = bundles;
        _clock = clock;
        _iteration = iteration;
    }

    internal DynamicBuffer<T> BorrowWrite<T>(Entity entity)
        where T : struct, IBufferElement
    {
        return BorrowWrite<T>(entity, _clock.Tick);
    }

    internal DynamicBuffer<T> BorrowWrite<T>(Entity entity, uint writeVersion)
        where T : struct, IBufferElement
    {
        Resolve<T>(
            entity,
            out Chunk chunk,
            out int row,
            out int headerColumn,
            out int inlineColumn);

        return new DynamicBuffer<T>(
            this,
            chunk,
            row,
            headerColumn,
            inlineColumn,
            writeVersion);
    }

    internal BufferView<T> BorrowRead<T>(Entity entity)
        where T : struct, IBufferElement
    {
        Resolve<T>(
            entity,
            out Chunk chunk,
            out int row,
            out int headerColumn,
            out int inlineColumn);

        return new BufferView<T>(chunk, row, headerColumn, inlineColumn);
    }

    private void Resolve<T>(
        Entity entity,
        out Chunk chunk,
        out int row,
        out int headerColumn,
        out int inlineColumn)
        where T : struct, IBufferElement
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;
        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();

        if (!archetype.TryColumn(headerId, out headerColumn) ||
            !archetype.TryColumn(inlineId, out inlineColumn))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have buffer component {typeof(T).Name}.");
        }

        chunk = record.Chunk!;
        row = record.RowInChunk;
    }

    internal void Add<T>(Entity entity)
        where T : struct, IBufferElement
        => Add(entity, ReadOnlyMemory<T>.Empty);

    internal void Add<T>(Entity entity, ReadOnlyMemory<T> values)
        where T : struct, IBufferElement
    {
        _ = DynamicBufferLayout<T>.InlineCapacity;
        _iteration.Throw();
        Span<int> componentIds =
        [
            BufferComponents.Header<T>(),
            BufferComponents.Inline<T>(),
        ];
        ReadOnlyMemory<T> initial = values;
        _bundles.ExecuteAdd(
            entity,
            componentIds,
            ReadOnlySpan<int>.Empty,
            ref initial,
            static (BundleWriteView view, ref ReadOnlyMemory<T> state) =>
                view.WriteBuffer(in state));
    }

    internal bool Has<T>(Entity entity)
        where T : struct, IBufferElement
    {
        if (!_entities.Store.IsAlive(entity))
            return false;

        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        if (record.Archetype is null)
            return false;

        var archetype = record.Archetype;
        return archetype.HasComponent(BufferComponents.Header<T>()) &&
               archetype.HasComponent(BufferComponents.Inline<T>());
    }

    internal void Remove<T>(Entity entity)
        where T : struct, IBufferElement
    {
        _iteration.Throw();

        if (!Has<T>(entity))
            throw new InvalidOperationException(
                $"Entity {entity} does not have buffer component {typeof(T).Name}.");

        _components.Remove<DynamicBufferHeader<T>>(entity);
        _components.Remove<DynamicBufferInline<T>>(entity);
    }

    internal void CopyStorage<T>(
        Entity source,
        Entity target,
        bool added)
        where T : struct, IBufferElement
    {
        EntityRecord sourceRecord = _entities.ReadRow(source);
        EntityRecord targetRecord = _entities.ReadRow(target);

        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();

        var sourceChunk = RequireStorage<T>(
            source,
            in sourceRecord,
            headerId,
            inlineId,
            out int sourceHeaderColumn,
            out int sourceInlineColumn,
            out int sourceRow);
        var targetChunk = RequireStorage<T>(
            target,
            in targetRecord,
            headerId,
            inlineId,
            out int targetHeaderColumn,
            out int targetInlineColumn,
            out int targetRow);

        CopyStoragePayload<T>(
            sourceChunk,
            sourceRow,
            sourceHeaderColumn,
            sourceInlineColumn,
            targetChunk,
            targetRow,
            targetHeaderColumn,
            targetInlineColumn);

        MarkStorage(added, targetChunk, targetHeaderColumn, targetInlineColumn, targetRow);
    }

    private static Chunk RequireStorage<T>(
        Entity entity,
        in EntityRecord record,
        int headerId,
        int inlineId,
        out int headerColumn,
        out int inlineColumn,
        out int row)
        where T : struct, IBufferElement
    {
        if (!record.Archetype!.TryColumn(headerId, out headerColumn) ||
            !record.Archetype.TryColumn(inlineId, out inlineColumn))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have buffer component {typeof(T).Name}.");
        }

        row = record.RowInChunk;
        return record.Chunk!;
    }

    private static void CopyStoragePayload<T>(
        Chunk sourceChunk,
        int sourceRow,
        int sourceHeaderColumn,
        int sourceInlineColumn,
        Chunk targetChunk,
        int targetRow,
        int targetHeaderColumn,
        int targetInlineColumn)
        where T : struct, IBufferElement
    {
        var sourceHeader = sourceChunk.ReadComponent<DynamicBufferHeader<T>>(sourceHeaderColumn, sourceRow);
        ref readonly var sourceInline = ref sourceChunk.GetComponentReadOnlyRef<DynamicBufferInline<T>>(
            sourceInlineColumn,
            sourceRow);
        ref var targetHeader = ref targetChunk.GetComponentRef<DynamicBufferHeader<T>>(targetHeaderColumn, targetRow);
        ref var targetInline = ref targetChunk.GetComponentRef<DynamicBufferInline<T>>(targetInlineColumn, targetRow);

        int count = sourceHeader.Count;
        int inlineCapacity = DynamicBufferLayout<T>.InlineCapacity;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        ClearTargetOverflow(
            targetChunk,
            ref targetHeader,
            sourceHeader.OverflowBackingIdentity,
            containsReferences);

        targetHeader.InlineCapacity = inlineCapacity;
        targetHeader.Count = count;

        if (count <= inlineCapacity)
            CopyInlineStorage(
                targetChunk,
                sourceHeader,
                in sourceInline,
                ref targetHeader,
                ref targetInline,
                count,
                inlineCapacity);
        else
            CopyOverflowStorage(
                targetChunk,
                sourceHeader,
                in sourceInline,
                ref targetHeader,
                ref targetInline,
                count,
                inlineCapacity,
                containsReferences);
    }

    private static void ClearTargetOverflow<T>(
        Chunk targetChunk,
        ref DynamicBufferHeader<T> targetHeader,
        object? sourceOverflowIdentity,
        bool containsReferences)
        where T : struct, IBufferElement
    {
        if (containsReferences &&
            targetHeader.HasOverflow &&
            targetChunk.OwnsBufferOverflow(in targetHeader) &&
            !ReferenceEquals(
                targetHeader.OverflowBackingIdentity,
                sourceOverflowIdentity))
        {
            targetHeader.OverflowWriteSpan[
                ..Math.Min(targetHeader.Count, targetHeader.OverflowCapacity)].Clear();
        }
    }

    private static void CopyInlineStorage<T>(
        Chunk targetChunk,
        DynamicBufferHeader<T> sourceHeader,
        in DynamicBufferInline<T> sourceInline,
        ref DynamicBufferHeader<T> targetHeader,
        ref DynamicBufferInline<T> targetInline,
        int count,
        int inlineCapacity)
        where T : struct, IBufferElement
    {
        targetChunk.SetOwnedBufferOverflow(ref targetHeader, null);
        for (int i = 0; i < inlineCapacity; i++)
            targetInline[i] = default;

        CopyElements(
            sourceHeader,
            in sourceInline,
            ref targetInline,
            count);
    }

    private static void CopyOverflowStorage<T>(
        Chunk targetChunk,
        DynamicBufferHeader<T> sourceHeader,
        in DynamicBufferInline<T> sourceInline,
        ref DynamicBufferHeader<T> targetHeader,
        ref DynamicBufferInline<T> targetInline,
        int count,
        int inlineCapacity,
        bool containsReferences)
        where T : struct, IBufferElement
    {
        var overflow = new T[Math.Max(count, Math.Max(1, inlineCapacity * 2))];
        CopyElements(
            sourceHeader,
            in sourceInline,
            overflow.AsSpan(0, count));

        targetChunk.SetOwnedBufferOverflow(ref targetHeader, overflow);
        if (!containsReferences)
            return;

        for (int i = 0; i < inlineCapacity; i++)
            targetInline[i] = default;
    }

    private void MarkStorage(
        bool added,
        Chunk targetChunk,
        int targetHeaderColumn,
        int targetInlineColumn,
        int targetRow)
    {
        if (added)
        {
            MarkAdd(targetChunk, targetHeaderColumn, targetRow);
            MarkAdd(targetChunk, targetInlineColumn, targetRow);
            return;
        }

        MarkWrite(targetChunk, targetHeaderColumn, targetRow);
        MarkWrite(targetChunk, targetInlineColumn, targetRow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAdd(Chunk chunk, int columnIndex, int row)
    {
        MarkAdd(chunk, columnIndex, row, _clock.Tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAdd(
        Chunk chunk,
        int columnIndex,
        int row,
        uint writeVersion)
    {
        chunk.MarkAdd(columnIndex, row, writeVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWrite(Chunk chunk, int columnIndex, int row)
    {
        MarkWrite(chunk, columnIndex, row, _clock.Tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWrite(
        Chunk chunk,
        int columnIndex,
        int row,
        uint writeVersion)
    {
        chunk.MarkWrite(columnIndex, row, writeVersion);
    }

    private static void CopyElements<T>(
        DynamicBufferHeader<T> sourceHeader,
        in DynamicBufferInline<T> sourceInline,
        ref DynamicBufferInline<T> targetInline,
        int count)
        where T : struct, IBufferElement
    {
        for (int i = 0; i < count; i++)
        {
            targetInline[i] = sourceHeader.HasOverflow
                ? sourceHeader.OverflowReadSpan[i]
                : sourceInline[i];
        }
    }

    private static void CopyElements<T>(
        DynamicBufferHeader<T> sourceHeader,
        in DynamicBufferInline<T> sourceInline,
        Span<T> destination)
        where T : struct, IBufferElement
    {
        if (sourceHeader.HasOverflow)
        {
            sourceHeader.OverflowReadSpan[..destination.Length].CopyTo(destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = sourceInline[i];
    }
}



