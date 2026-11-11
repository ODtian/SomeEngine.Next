using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    public void Create<T1, T2, T3>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third)
        where T1 : struct where T2 : struct where T3 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasValueOperationsType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasValueOperationsType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasValueOperationsType || ComponentType<T3>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second, third);
            return;
        }
        RequireStructure();
        Archetype archetype = Find(TypeSet<T1, T2, T3>.Value);
        if (ChunkCapacity == 0) { CreateRows(entities, first, second, third, archetype.PrimaryChunk); return; }
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second, third, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        if (!ComponentType<T1>.IsTagType) first[..count].CopyTo(ColumnEntry.Span<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], start + count)[start..]);
        if (!ComponentType<T2>.IsTagType) ColumnEntry.Span<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], start + count)[start..].Fill(second);
        if (!ComponentType<T3>.IsTagType) ColumnEntry.Span<T3>(ref chunk.Columns[ComponentType<T3>.TypeId], start + count)[start..].Fill(third);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType)
            for (int i = 0; i < count; i++) IndexCreated(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, third);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

    public void Create<T1, T2, T3, T4>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third, in T4 fourth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasValueOperationsType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasValueOperationsType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasValueOperationsType || ComponentType<T3>.HasInitializerType
            || ComponentType<T4>.IsSharedType || ComponentType<T4>.HasValueOperationsType || ComponentType<T4>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second, third, fourth);
            return;
        }
        RequireStructure();
        Archetype archetype = Find(TypeSet<T1, T2, T3, T4>.Value);
        if (ChunkCapacity == 0) { CreateRows(entities, first, second, third, fourth, archetype.PrimaryChunk); return; }
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second, third, fourth, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3, T4>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third, in T4 fourth, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        if (!ComponentType<T1>.IsTagType) first[..count].CopyTo(ColumnEntry.Span<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], start + count)[start..]);
        if (!ComponentType<T2>.IsTagType) ColumnEntry.Span<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], start + count)[start..].Fill(second);
        if (!ComponentType<T3>.IsTagType) ColumnEntry.Span<T3>(ref chunk.Columns[ComponentType<T3>.TypeId], start + count)[start..].Fill(third);
        if (!ComponentType<T4>.IsTagType) ColumnEntry.Span<T4>(ref chunk.Columns[ComponentType<T4>.TypeId], start + count)[start..].Fill(fourth);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType)
            for (int i = 0; i < count; i++) IndexCreated(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, fourth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

    public void Create<T1, T2, T3, T4, T5>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasValueOperationsType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasValueOperationsType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasValueOperationsType || ComponentType<T3>.HasInitializerType
            || ComponentType<T4>.IsSharedType || ComponentType<T4>.HasValueOperationsType || ComponentType<T4>.HasInitializerType
            || ComponentType<T5>.IsSharedType || ComponentType<T5>.HasValueOperationsType || ComponentType<T5>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second, third, fourth, fifth);
            return;
        }
        RequireStructure();
        Archetype archetype = Find(TypeSet<T1, T2, T3, T4, T5>.Value);
        if (ChunkCapacity == 0) { CreateRows(entities, first, second, third, fourth, fifth, archetype.PrimaryChunk); return; }
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second, third, fourth, fifth, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3, T4, T5>(Span<Entity> entities, ReadOnlySpan<T1> first, in T2 second, in T3 third, in T4 fourth, in T5 fifth, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        if (!ComponentType<T1>.IsTagType) first[..count].CopyTo(ColumnEntry.Span<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], start + count)[start..]);
        if (!ComponentType<T2>.IsTagType) ColumnEntry.Span<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], start + count)[start..].Fill(second);
        if (!ComponentType<T3>.IsTagType) ColumnEntry.Span<T3>(ref chunk.Columns[ComponentType<T3>.TypeId], start + count)[start..].Fill(third);
        if (!ComponentType<T4>.IsTagType) ColumnEntry.Span<T4>(ref chunk.Columns[ComponentType<T4>.TypeId], start + count)[start..].Fill(fourth);
        if (!ComponentType<T5>.IsTagType) ColumnEntry.Span<T5>(ref chunk.Columns[ComponentType<T5>.TypeId], start + count)[start..].Fill(fifth);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType)
            for (int i = 0; i < count; i++) IndexCreated(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, third);
        if (ComponentType<T4>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, fourth);
        if (ComponentType<T5>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, fifth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

}
