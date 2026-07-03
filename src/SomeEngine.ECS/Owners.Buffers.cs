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
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Buffers
{
    private Entities _entities = null!;
    private Components _components = null!;
    private Bundles _bundles = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;

    internal void Bind(
        Entities entities,
        Components components,
        Bundles bundles,
        Journal journal,
        Clock clock,
        Iteration iteration)
    {
        _entities = entities;
        _components = components;
        _bundles = bundles;
        _journal = journal;
        _clock = clock;
        _iteration = iteration;
    }

    internal DynamicBuffer<T> Get<T>(Entity entity)
        where T : struct, IBufferElement
    {
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();

        if (!archetype.TryColumn(headerId, out int headerColumn) ||
            !archetype.TryColumn(inlineId, out int inlineColumn))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have buffer component {typeof(T).Name}.");
        }

        ref var header = ref record.Chunk!.GetComponentRef<DynamicBufferHeader<T>>(
            headerColumn,
            record.RowInChunk);
        if (header.InlineCapacity != DynamicBufferLayout<T>.InlineCapacity)
            header.InlineCapacity = DynamicBufferLayout<T>.InlineCapacity;

        return new DynamicBuffer<T>(
            this,
            record.Chunk!,
            record.RowInChunk,
            headerColumn,
            inlineColumn);
    }

    internal void Add<T>(Entity entity)
        where T : struct, IBufferElement
    {
        _ = DynamicBufferLayout<T>.InlineCapacity;
        Add(
            entity,
            BufferComponents.Header<T>(),
            BufferComponents.Inline<T>(),
            typeof(T).Name,
            DynamicBufferHeader<T>.Create(),
            default(DynamicBufferInline<T>));
    }

    private void Add<T>(
        Entity entity,
        int headerId,
        int inlineId,
        string name,
        DynamicBufferHeader<T> header,
        DynamicBufferInline<T> inline)
        where T : struct, IBufferElement
    {
        _iteration.Throw();
        var context = CreateBufferAddWriter<T>(entity, headerId, inlineId, name);
        context.Write(header);
        context.Write(inline);
        Write(SerializationChangeKind.BufferAdded, entity, headerId);
    }

    private BundleWriter CreateBufferAddWriter<T>(
        Entity entity,
        int headerId,
        int inlineId,
        string name)
        where T : struct, IBufferElement
    {
        ref var record = ref _entities.Row(entity);
        var sourceArchetype = record.Archetype!;
        bool hadHeader = sourceArchetype.HasComponent(headerId);
        bool hadInline = sourceArchetype.HasComponent(inlineId);
        if (hadHeader || hadInline)
            throw new InvalidOperationException(
                $"Entity {entity} already has buffer component {name}.");

        Span<int> componentIds = stackalloc int[(hadHeader ? 0 : 1) + (hadInline ? 0 : 1)];
        int index = 0;
        if (!hadHeader)
            componentIds[index++] = headerId;
        if (!hadInline)
            componentIds[index++] = inlineId;

        return _bundles.CreateAddWriter(
            entity,
            componentIds,
            ReadOnlySpan<SharedValueSlot>.Empty,
            ReadOnlySpan<int>.Empty);
    }

    internal bool Has<T>(Entity entity)
        where T : struct, IBufferElement
    {
        if (!_entities.Store.IsAlive(entity))
            return false;

        ref var record = ref _entities.Store.GetRecord(entity);
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
        Write(SerializationChangeKind.BufferRemoved, entity, BufferComponents.Header<T>());
    }

    internal void CopyStorage<T>(
        Entity source,
        Entity target,
        SerializationChangeKind kind)
        where T : struct, IBufferElement
    {
        ref var sourceRecord = ref _entities.Row(source);
        ref var targetRecord = ref _entities.Row(target);

        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();

        var sourceChunk = RequireStorage<T>(
            source,
            ref sourceRecord,
            headerId,
            inlineId,
            out int sourceHeaderColumn,
            out int sourceInlineColumn,
            out int sourceRow);
        var targetChunk = RequireStorage<T>(
            target,
            ref targetRecord,
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

        MarkStorage(kind, targetChunk, targetHeaderColumn, targetInlineColumn, targetRow);

        Write(kind, target, headerId);
    }

    private static Chunk RequireStorage<T>(
        Entity entity,
        ref EntityRecord record,
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
        ref var sourceInline = ref sourceChunk.GetComponentRef<DynamicBufferInline<T>>(sourceInlineColumn, sourceRow);
        ref var targetHeader = ref targetChunk.GetComponentRef<DynamicBufferHeader<T>>(targetHeaderColumn, targetRow);
        ref var targetInline = ref targetChunk.GetComponentRef<DynamicBufferInline<T>>(targetInlineColumn, targetRow);

        int count = sourceHeader.Count;
        int inlineCapacity = DynamicBufferLayout<T>.InlineCapacity;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        ClearTargetOverflow(ref targetHeader, sourceHeader.Overflow, containsReferences);

        targetHeader.InlineCapacity = inlineCapacity;
        targetHeader.Count = count;

        if (count <= inlineCapacity)
            CopyInlineStorage(sourceHeader, ref sourceInline, ref targetHeader, ref targetInline, count, inlineCapacity);
        else
            CopyOverflowStorage(sourceHeader, ref sourceInline, ref targetHeader, ref targetInline, count, inlineCapacity, containsReferences);
    }

    private static void ClearTargetOverflow<T>(
        ref DynamicBufferHeader<T> targetHeader,
        T[]? sourceOverflow,
        bool containsReferences)
        where T : struct, IBufferElement
    {
        var oldTargetOverflow = targetHeader.Overflow;
        if (containsReferences &&
            oldTargetOverflow is not null &&
            !ReferenceEquals(oldTargetOverflow, sourceOverflow))
        {
            oldTargetOverflow.AsSpan(0, Math.Min(targetHeader.Count, oldTargetOverflow.Length)).Clear();
        }
    }

    private static void CopyInlineStorage<T>(
        DynamicBufferHeader<T> sourceHeader,
        ref DynamicBufferInline<T> sourceInline,
        ref DynamicBufferHeader<T> targetHeader,
        ref DynamicBufferInline<T> targetInline,
        int count,
        int inlineCapacity)
        where T : struct, IBufferElement
    {
        targetHeader.Overflow = null;
        for (int i = 0; i < inlineCapacity; i++)
            targetInline.Elements[i] = default;

        CopyElements(
            sourceHeader,
            ref sourceInline,
            ref targetInline,
            count);
    }

    private static void CopyOverflowStorage<T>(
        DynamicBufferHeader<T> sourceHeader,
        ref DynamicBufferInline<T> sourceInline,
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
            ref sourceInline,
            overflow.AsSpan(0, count));

        targetHeader.Overflow = overflow;
        if (!containsReferences)
            return;

        for (int i = 0; i < inlineCapacity; i++)
            targetInline.Elements[i] = default;
    }

    private void MarkStorage(
        SerializationChangeKind kind,
        Chunk targetChunk,
        int targetHeaderColumn,
        int targetInlineColumn,
        int targetRow)
    {
        if (kind == SerializationChangeKind.BufferAdded)
        {
            MarkAdd(targetChunk, targetHeaderColumn, targetRow);
            MarkAdd(targetChunk, targetInlineColumn, targetRow);
            return;
        }

        MarkChunk(targetChunk, targetHeaderColumn);
        MarkChunk(targetChunk, targetInlineColumn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAdd(Chunk chunk, int columnIndex, int row)
    {
        chunk.MarkAdd(columnIndex, row, _clock.Tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkChunk(Chunk chunk, int columnIndex)
    {
        chunk.MarkChunk(columnIndex, _clock.Tick);
    }

    internal void Write(SerializationChangeKind kind, Entity entity, int componentId)
    {
        _journal.Write(kind, entity, componentId, default, _clock.Tick);
    }

    private static void CopyElements<T>(
        DynamicBufferHeader<T> sourceHeader,
        ref DynamicBufferInline<T> sourceInline,
        ref DynamicBufferInline<T> targetInline,
        int count)
        where T : struct, IBufferElement
    {
        for (int i = 0; i < count; i++)
        {
            targetInline.Elements[i] = sourceHeader.Overflow is not null
                ? sourceHeader.Overflow[i]
                : sourceInline.Elements[i];
        }
    }

    private static void CopyElements<T>(
        DynamicBufferHeader<T> sourceHeader,
        ref DynamicBufferInline<T> sourceInline,
        Span<T> destination)
        where T : struct, IBufferElement
    {
        if (sourceHeader.Overflow is not null)
        {
            sourceHeader.Overflow.AsSpan(0, destination.Length).CopyTo(destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = sourceInline.Elements[i];
    }
}



