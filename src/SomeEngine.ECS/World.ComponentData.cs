using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    public bool HasComponentData<T>(Entity entity) where T : struct
        => ComponentType<T>.IsSparseType ? HasSparse<T>(entity) : Has<T>(entity);

    public bool HasComponentData(Entity entity, int component)
        => ComponentDataWriteVersion(entity, component).HasValue;

    public T ReadComponentData<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenValues([new(ComponentType<T>.TypeId,
            ComponentType<T>.IsSparseType ? WorldValueKind.Sparse : WorldValueKind.Table, false, false,
            !ComponentType<T>.IsSparseType && ComponentType<T>.Value.UsesStructureWrites)]);
        if (ComponentType<T>.IsTagType) return default;
        return ComponentType<T>.IsSparseType ? ReadSparse<T>(entity) : Read<T>(entity);
    }

    public ref readonly T ReadComponentReference<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenValues([new(ComponentType<T>.TypeId,
            ComponentType<T>.IsSparseType ? WorldValueKind.Sparse : WorldValueKind.Table, false, false,
            !ComponentType<T>.IsSparseType && ComponentType<T>.Value.UsesStructureWrites)]);
        if (ComponentType<T>.IsSparseType) return ref ReadSparse<T>(entity);
        return ref Read<T>(entity);
    }

    public void AddComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsSparseType) AddSparse(entity, value);
        else Add(entity, value);
    }

    public void ReplaceComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsSparseType) ReplaceSparse(entity, value);
        else Set(entity, value);
    }

    /// <summary>Stores an equal value while preserving its observation version.</summary>
    public void OverwriteComponentData<T>(Entity entity, in T value) where T : struct
    {
        using WorldAccess access = OpenValues([new(ComponentType<T>.TypeId,
            ComponentType<T>.IsSparseType ? WorldValueKind.Sparse : WorldValueKind.Table, true, false)]);
        if (ComponentType<T>.IsTagType || ComponentType<T>.IsSharedType) return;
        if (ComponentType<T>.IsSparseType) Sparse<T>().Get(entity) = value;
        else
        {
            ref EntityRecord record = ref Resolve(entity);
            ColumnEntry.At<T>(ref record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row) = value;
        }
    }

    /// <summary>Discards data introduced by a build that is restoring its previous result.</summary>
    public void DiscardComponentData<T>(Entity entity) where T : struct
    {
        if (ComponentType<T>.IsSparseType) { RemoveSparse<T>(entity); return; }
        using WorldAccess access = OpenStructure();
        RequireStructure();
        using MembershipEdit membership = BeginMembershipEdit(entity);
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        if (ComponentType<T>.TracksWritesType)
        {
            ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[type].ColumnPlusOne - 1];
            changes.LastWrite = Math.Max(changes.LastWrite, NextVersion());
        }
        if (ComponentType<T>.IsIndexedType) ActiveIndex<T>()?.Remove(entity.Id);
        if (ComponentType<T>.IsSharedType) RemoveSharedMember(entity, chunk, type, record.Row);
        else if (ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(this,
            ref ColumnEntry.At<T>(ref chunk.Columns[type], record.Row));
        if (_retainRemovedColumns)
        {
            EnsurePresence(chunk, type)[record.Row >> 6] &= ~(1UL << (record.Row & 63));
            chunk.RemoveEnabled(type, record.Row);
            if (!ComponentType<T>.IsTagType && !ComponentType<T>.IsSharedType)
                ColumnEntry.At<T>(ref chunk.Columns[type], record.Row) = default;
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Remove(entity);
        }
        else MoveKnown(entity, ref record, chunk, GetTransition(chunk, type, false, record.Row));
    }

    public ulong ComponentDataWriteVersion<T>(Entity entity) where T : struct
        => ComponentDataWriteVersion(entity, ComponentType<T>.TypeId) ?? 0;

    public ulong? ComponentDataWriteVersion(Entity entity, int component)
    {
        ComponentType type = ComponentTypes.Get(component);
        using WorldAccess access = OpenValues([new(component,
            type.IsSparse ? WorldValueKind.Sparse : WorldValueKind.Table, false, type.TracksWrites)]);
        if (!IsAliveCore(entity)) return null;
        if (type.IsSparse)
        {
            SparseStore? store = _sparse is { } sparse && (uint)component < (uint)sparse.Types.Length
                ? sparse.Types[component] : null;
            if (store?.Has(entity) != true) return null;
            return type.TracksWrites ? store.Changes![0].WrittenAt(store.Find(entity.Id).RowPlusOne - 1, type.TracksAdded) : 0;
        }
        ref EntityRecord record = ref Records[entity.Id];
        Chunk chunk = record.Chunk!;
        if ((uint)component >= (uint)chunk.Columns.Length || !chunk.Columns[component].Has(record.Row)) return null;
        return type.TracksWrites
            ? chunk.Changes![chunk.Columns[component].ColumnPlusOne - 1].WrittenAt(record.Row, type.TracksAdded) : 0;
    }

    public ulong ComponentWriteVersion(int component)
    {
        ComponentType type = ComponentTypes.Get(component);
        using WorldAccess access = OpenValues([new(component,
            type.IsSparse ? WorldValueKind.Sparse : WorldValueKind.Table, false, type.TracksWrites)]);
        if (!type.TracksWrites) return 0;
        if (type.IsSparse)
            return _sparse is { } sparse && (uint)component < (uint)sparse.Types.Length
                ? sparse.Types[component]?.Changes?[0].LastWrite ?? 0 : 0;
        ulong version = 0;
        for (int index = 0; index < ArchetypeCount; index++)
            foreach (Chunk chunk in Archetypes[index].AllChunks)
                if ((uint)component < (uint)chunk.Columns.Length && chunk.Columns[component].ColumnPlusOne != 0)
                    version = Math.Max(version, chunk.Changes![chunk.Columns[component].ColumnPlusOne - 1].LastWrite);
        return version;
    }
}
