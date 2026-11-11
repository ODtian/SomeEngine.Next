using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private void SetBatchWithExecution<T>(ReadOnlySpan<Entity> entities, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
        {
            using WorldAccess structure = OpenStructure();
            SetBatchCore(entities, value);
            return;
        }
        using (WorldAccess access = OpenValues<T>(true))
        {
            bool allPresent = true;
            int type = ComponentType<T>.TypeId;
            foreach (Entity entity in entities)
            {
                ref EntityRecord record = ref Resolve(entity);
                ColumnEntry[] columns = record.Chunk!.Columns;
                if ((uint)type >= (uint)columns.Length || !columns[type].Has(record.Row)) { allPresent = false; break; }
            }
            if (allPresent)
            {
                ulong version = 0;
                foreach (Entity entity in entities)
                {
                    ref EntityRecord record = ref Records[entity.Id];
                    if (ComponentType<T>.TracksWritesType) WriteVersion(ref version);
                    ReplacePresentValue(record.Chunk!, record.Row, value, version);
                }
                return;
            }
        }
        using WorldAccess addition = OpenStructure();
        SetBatchCore(entities, value);
    }

    private void SetWithExecution<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
        {
            using WorldAccess structure = OpenStructure();
            SetComponent(entity, value);
            return;
        }
        using (WorldAccess access = OpenValues<T>(true))
        {
            ref EntityRecord record = ref Resolve(entity);
            RequireActiveWrite<T>(in record);
            int type = ComponentType<T>.TypeId;
            ColumnEntry[] columns = record.Chunk!.Columns;
            if ((uint)type < (uint)columns.Length && columns[type].Has(record.Row))
            {
                ReplacePresentValue(record.Chunk, record.Row, value);
                return;
            }
        }
        using WorldAccess addition = OpenStructure();
        SetComponent(entity, value);
    }

    private void SetSparseWithExecution<T>(Entity entity, in T value) where T : struct
    {
        using (WorldAccess access = OpenValues<T>(true, WorldValueKind.Sparse))
        {
            ref EntityRecord record = ref Resolve(entity);
            RequireActiveWrite<T>(in record);
            if (_sparse?.Find<T>() is { } store && store.Has(entity.Id))
            {
                store.Set(this, _sparse, entity, value);
                return;
            }
        }
        using WorldAccess addition = OpenStructure();
        ref EntityRecord added = ref Resolve(entity);
        RequireActiveWrite<T>(in added);
        Sparse<T>().Set(this, _sparse!, entity, value);
    }

    private void SetResourceWithExecution<T>(in T value)
    {
        using (WorldAccess access = OpenResourceValues<T>(true))
            if (_resources.Get(ResourceType<T>.Id) is { HasValue: true } existing)
            {
                System.Runtime.CompilerServices.Unsafe.As<ResourceValue<T>>(existing).Replace(value);
                return;
            }
        using WorldAccess addition = OpenStructure();
        SetResourceCore(value);
    }

    private void ReserveTypes(TypeSet types, int capacity)
    {
        using WorldAccess access = OpenStructure();
        foreach (PodColumn column in types.Columns.Pod)
            PodBuffer(ComponentTypes.Get(column.TypeId), Records.Length - 1);
        Find(types).ReserveRows(capacity);
    }
}
