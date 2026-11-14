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


}
