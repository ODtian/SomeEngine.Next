using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    public void Set<T>(ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values) where T : struct
    {
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping
            && (!ComponentType<T>.IsIndexedType || ActiveIndex<T>() is null))
        {
            using WorldAccess access = OpenStructure();
            grouping.Set(entities, values);
            return;
        }
        for (int i = 0; i < entities.Length; i++) Set(entities[i], values[i]);
    }

    public void Set<T>(ReadOnlySpan<Entity> entities, in T value) where T : struct
    {
        if (Execution is not null) { SetBatchWithExecution(entities, value); return; }
        SetBatchCore(entities, value);
    }

    private void SetBatchCore<T>(ReadOnlySpan<Entity> entities, in T value) where T : struct
    {
        if (ComponentType<T>.IsIndexedType && ActiveIndex<T>() is { } index)
        {
            foreach (Entity entity in entities) index.Set(entity, value);
            return;
        }
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping)
        {
            foreach (Entity entity in entities) grouping.Set(entity, value);
            return;
        }
        if (ComponentType<T>.IsSharedType)
        {
            RequireStructure();
            int shared = Shared<T>().Intern(value);
            foreach (Entity entity in entities)
            {
                ref EntityRecord sharedRecord = ref Resolve(entity);
                SetSharedId<T>(entity, ref sharedRecord, shared);
            }
            return;
        }
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        EntityRecord[] records = Records;
        int used = Used;
        int type = ComponentType<T>.TypeId;
        T repeated = value;
        ulong version = 0;
        foreach (Entity entity in entities)
        {
            if ((uint)(entity.Id - 1) >= (uint)used) throw new ArgumentException("Entity id is outside this world.");
            ref EntityRecord record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(records), entity.Id);
            if (record.Generation != entity.Generation) throw new ArgumentException("Entity generation has expired.");
            using MembershipEdit membership = BeginMembershipEdit(entity);
            ColumnEntry[] columns = record.Chunk!.Columns;
            if ((uint)type < (uint)columns.Length && columns[type].ColumnPlusOne != 0)
            {
                bool added = !columns[type].Has(record.Row);
                if (added)
                {
                    RequireStructure();
                    record.Chunk.RestorePresence(type, record.Row);
                }
                else if (ComponentType<T>.HasValueOperationsType)
                    ComponentType<T>.ReleaseValue(this, ref ColumnEntry.At<T>(ref columns[type], record.Row));
                if (!ComponentType<T>.IsTagType) ColumnEntry.At<T>(ref columns[type], record.Row) = repeated;
                if (added && (uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Bind(entity, record.Chunk, record.Row);
                if (ComponentType<T>.TracksWritesType) Stamp<T>(record.Chunk, record.Row, WriteVersion(ref version), added);
            }
            else AddKnown(entity, ref record, repeated, ComponentType<T>.TracksWritesType ? WriteVersion(ref version) : 0);
        }
    }

    public void Create<T1, T2, T3>(Span<Entity> entities, in T1 first, in T2 second, in T3 third)
        where T1 : struct where T2 : struct where T3 : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<T1>.IsSharedType || ComponentType<T2>.IsSharedType || ComponentType<T3>.IsSharedType
            || ComponentType<T1>.HasValueOperationsType || ComponentType<T2>.HasValueOperationsType || ComponentType<T3>.HasValueOperationsType
            || ComponentType<T1>.HasInitializerType || ComponentType<T2>.HasInitializerType || ComponentType<T3>.HasInitializerType)
        {
            for (int createdIndex = 0; createdIndex < entities.Length; createdIndex++)
                entities[createdIndex] = Create(first, second, third);
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
            CreateRows(entities.Slice(offset, count), first, second, third, chunk);
            offset += count;
        }
    }

    private void CreateRows<T1, T2, T3>(Span<Entity> entities, in T1 first, in T2 second, in T3 third, Chunk chunk)
        where T1 : struct where T2 : struct where T3 : struct
    {
        int start = AllocateCreatedRows(entities, chunk);
        int count = entities.Length;
        if (!ComponentType<T1>.IsTagType) ColumnEntry.Span<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], start + count)[start..].Fill(first);
        if (!ComponentType<T2>.IsTagType) ColumnEntry.Span<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], start + count)[start..].Fill(second);
        if (!ComponentType<T3>.IsTagType) ColumnEntry.Span<T3>(ref chunk.Columns[ComponentType<T3>.TypeId], start + count)[start..].Fill(third);
        CompleteCreatedRows(chunk, start, count);
        if (ComponentType<T1>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, first);
        if (ComponentType<T2>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, second);
        if (ComponentType<T3>.IsIndexedType)
            foreach (Entity entity in entities) IndexCreated(entity, third);
        if (ComponentType<T1>.IsGroupedType || ComponentType<T2>.IsGroupedType || ComponentType<T3>.IsGroupedType)
            foreach (Entity entity in entities) ArrangeCreatedGroups(entity);
    }
}
