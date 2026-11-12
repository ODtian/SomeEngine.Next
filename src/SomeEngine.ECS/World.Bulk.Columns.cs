using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private void CopyCreatedValues<T>(ref ColumnEntry column, int start, int count, ReadOnlySpan<T> values) where T : struct
    {
        if (ComponentType<T>.IsTagType) return;
        Span<T> target = ColumnEntry.Span<T>(ref column, start + count).Slice(start, count);
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < count; i++) target[i] = ComponentType<T>.CloneValue(this, values[i]);
        else values[..count].CopyTo(target);
    }

    private void FillCreatedValues<T>(ref ColumnEntry column, int start, int count, in T value) where T : struct
    {
        if (ComponentType<T>.IsTagType) return;
        Span<T> target = ColumnEntry.Span<T>(ref column, start + count).Slice(start, count);
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < count; i++) target[i] = ComponentType<T>.CloneValue(this, value);
        else target.Fill(value);
    }

    public void Create<T1, T2, T3, T4>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasInitializerType
            || ComponentType<T4>.IsSharedType || ComponentType<T4>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second[i], third, fourth);
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
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second.Slice(offset, count), third, fourth, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3, T4>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        CopyCreatedValues(ref chunk.Columns[ComponentType<T1>.TypeId], start, count, first);
        CopyCreatedValues(ref chunk.Columns[ComponentType<T2>.TypeId], start, count, second);
        FillCreatedValues(ref chunk.Columns[ComponentType<T3>.TypeId], start, count, third);
        FillCreatedValues(ref chunk.Columns[ComponentType<T4>.TypeId], start, count, fourth);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType && ActiveIndex<T1>() is { } index1)
            for (int i = 0; i < count; i++) index1.Added(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType && ActiveIndex<T2>() is { } index2)
            for (int i = 0; i < count; i++) index2.Added(entities[i], second[i]);
        if (ComponentType<T3>.IsIndexedType && ActiveIndex<T3>() is { } index3)
            for (int i = 0; i < count; i++) index3.Added(entities[i], third);
        if (ComponentType<T4>.IsIndexedType && ActiveIndex<T4>() is { } index4)
            for (int i = 0; i < count; i++) index4.Added(entities[i], fourth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

    public void Create<T1, T2, T3, T4, T5>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth, in T5 fifth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasInitializerType
            || ComponentType<T4>.IsSharedType || ComponentType<T4>.HasInitializerType
            || ComponentType<T5>.IsSharedType || ComponentType<T5>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second[i], third, fourth, fifth);
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
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second.Slice(offset, count), third, fourth, fifth, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3, T4, T5>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth, in T5 fifth, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        CopyCreatedValues(ref chunk.Columns[ComponentType<T1>.TypeId], start, count, first);
        CopyCreatedValues(ref chunk.Columns[ComponentType<T2>.TypeId], start, count, second);
        FillCreatedValues(ref chunk.Columns[ComponentType<T3>.TypeId], start, count, third);
        FillCreatedValues(ref chunk.Columns[ComponentType<T4>.TypeId], start, count, fourth);
        FillCreatedValues(ref chunk.Columns[ComponentType<T5>.TypeId], start, count, fifth);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType && ActiveIndex<T1>() is { } index1)
            for (int i = 0; i < count; i++) index1.Added(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType && ActiveIndex<T2>() is { } index2)
            for (int i = 0; i < count; i++) index2.Added(entities[i], second[i]);
        if (ComponentType<T3>.IsIndexedType && ActiveIndex<T3>() is { } index3)
            for (int i = 0; i < count; i++) index3.Added(entities[i], third);
        if (ComponentType<T4>.IsIndexedType && ActiveIndex<T4>() is { } index4)
            for (int i = 0; i < count; i++) index4.Added(entities[i], fourth);
        if (ComponentType<T5>.IsIndexedType && ActiveIndex<T5>() is { } index5)
            for (int i = 0; i < count; i++) index5.Added(entities[i], fifth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

    public void Create<T1, T2, T3, T4, T5, T6>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T1>.HasInitializerType
            || ComponentType<T2>.IsSharedType || ComponentType<T2>.HasInitializerType
            || ComponentType<T3>.IsSharedType || ComponentType<T3>.HasInitializerType
            || ComponentType<T4>.IsSharedType || ComponentType<T4>.HasInitializerType
            || ComponentType<T5>.IsSharedType || ComponentType<T5>.HasInitializerType
            || ComponentType<T6>.IsSharedType || ComponentType<T6>.HasInitializerType)
        {
            for (int i = 0; i < entities.Length; i++) entities[i] = Create(first[i], second[i], third, fourth, fifth, sixth);
            return;
        }
        RequireStructure();
        Archetype archetype = Find(TypeSet<T1, T2, T3, T4, T5, T6>.Value);
        if (ChunkCapacity == 0) { CreateRows(entities, first, second, third, fourth, fifth, sixth, archetype.PrimaryChunk); return; }
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            CreateRows(entities.Slice(offset, count), first.Slice(offset, count), second.Slice(offset, count), third, fourth, fifth, sixth, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3, T4, T5, T6>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth, in T5 fifth, in T6 sixth, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
    {
        int start = AllocateCreatedRows(entities, chunk), count = entities.Length;
        CopyCreatedValues(ref chunk.Columns[ComponentType<T1>.TypeId], start, count, first);
        CopyCreatedValues(ref chunk.Columns[ComponentType<T2>.TypeId], start, count, second);
        FillCreatedValues(ref chunk.Columns[ComponentType<T3>.TypeId], start, count, third);
        FillCreatedValues(ref chunk.Columns[ComponentType<T4>.TypeId], start, count, fourth);
        FillCreatedValues(ref chunk.Columns[ComponentType<T5>.TypeId], start, count, fifth);
        FillCreatedValues(ref chunk.Columns[ComponentType<T6>.TypeId], start, count, sixth);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType && ActiveIndex<T1>() is { } index1)
            for (int i = 0; i < count; i++) index1.Added(entities[i], first[i]);
        if (ComponentType<T2>.IsIndexedType && ActiveIndex<T2>() is { } index2)
            for (int i = 0; i < count; i++) index2.Added(entities[i], second[i]);
        if (ComponentType<T3>.IsIndexedType && ActiveIndex<T3>() is { } index3)
            for (int i = 0; i < count; i++) index3.Added(entities[i], third);
        if (ComponentType<T4>.IsIndexedType && ActiveIndex<T4>() is { } index4)
            for (int i = 0; i < count; i++) index4.Added(entities[i], fourth);
        if (ComponentType<T5>.IsIndexedType && ActiveIndex<T5>() is { } index5)
            for (int i = 0; i < count; i++) index5.Added(entities[i], fifth);
        if (ComponentType<T6>.IsIndexedType && ActiveIndex<T6>() is { } index6)
            for (int i = 0; i < count; i++) index6.Added(entities[i], sixth);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType || ComponentType<T4>.IsGroupedType || ComponentType<T5>.IsGroupedType || ComponentType<T6>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }

}
