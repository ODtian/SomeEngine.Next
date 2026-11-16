using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Sparse;

internal struct SparseCell
{
    internal int RowPlusOne;
    internal int AttachmentPlusOne;
}

internal abstract class SparseStore
{
    internal SparseCell[]?[] Pages = [];
    internal Entity[] Entities = new Entity[16];
    internal int Count;
    internal ComponentChanges[]? Changes;
    internal abstract bool IsCleanup { get; }
    internal abstract bool KeepsRemoved { get; }
    internal abstract bool HasEntityReferences { get; }
    internal abstract ref byte Bytes(Entity entity, ulong writeVersion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref SparseCell Find(int entityId)
    {
        int page = entityId >> 12;
        if ((uint)page < (uint)Pages.Length && Pages[page] is { } cells)
            return ref cells[entityId & 4095];
        return ref Unsafe.NullRef<SparseCell>();
    }

    internal ref SparseCell EnsureCell(int entityId)
    {
        int page = entityId >> 12;
        if (page >= Pages.Length) Array.Resize(ref Pages, Math.Max(page + 1, Math.Max(4, Pages.Length * 2)));
        SparseCell[] cells = Pages[page] ??= new SparseCell[4096];
        return ref cells[entityId & 4095];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Has(int entityId)
    {
        ref SparseCell cell = ref Find(entityId);
        return !Unsafe.IsNullRef(ref cell) && cell.RowPlusOne != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Has(Entity entity)
    {
        ref SparseCell cell = ref Find(entity.Id);
        return !Unsafe.IsNullRef(ref cell) && cell.RowPlusOne != 0
            && Entities[cell.RowPlusOne - 1].Generation == entity.Generation;
    }

    internal abstract void RemoveKnown(World world, int entityId);
    internal abstract void RemoveForDestroy(World world, Entity entity);
    internal abstract bool HasChanged(int entityId, ulong since, bool added);
    internal abstract void Clone(World world, SparseStores stores, int sourceId, Entity target);
    internal abstract void CloneTo(World world, int sourceId, Entity target, in EntityMap entities);
    internal abstract void Clear(World world);
    internal abstract SparseStore CopyData(World values, SparseStore? target = null);
    internal abstract void RestoreData(World world, SparseStores stores, bool preserveProgress);
    internal abstract void ReleaseData(World values, bool keepCapacity = false);
}

internal sealed class SparseStore<T> : SparseStore where T : struct
{
    internal override ref byte Bytes(Entity entity, ulong writeVersion)
        => ref Unsafe.As<T, byte>(ref Get(entity, writeVersion));
    internal T[] Values = new T[16];
    internal SparseStore()
    {
        ComponentValues.Bind<T>();
        Changes = ComponentType<T>.TracksWritesType ? [new ComponentChanges(16, ComponentType<T>.TracksAddedType)] : null;
    }
    internal override bool IsCleanup => ComponentType<T>.IsCleanupType;
    internal override bool KeepsRemoved => ComponentType<T>.KeepsRemovedType;
    internal override bool HasEntityReferences => ComponentType<T>.Value.HasEntityReferences;

    internal override SparseStore CopyData(World values, SparseStore? target = null)
    {
        var copy = target as SparseStore<T> ?? new SparseStore<T>();
        if (copy.Entities.Length < Count) Array.Resize(ref copy.Entities, Count);
        if (copy.Values.Length < Count) Array.Resize(ref copy.Values, Count);
        Entities.AsSpan(0, Count).CopyTo(copy.Entities);
        copy.Count = Count;
        if (Changes is { } changes)
        {
            if (copy.Changes![0].Rows.Length < Count) copy.Changes[0].Grow(Count, ComponentType<T>.TracksAddedType);
            changes[0].CopyTo(ref copy.Changes[0], Count);
        }
        if (ComponentType<T>.HasValueOperationsType)
            for (int row = 0; row < Count; row++) copy.Values[row] = ComponentType<T>.CloneValue(values, Values[row]);
        else Values.AsSpan(0, Count).CopyTo(copy.Values);
        return copy;
    }

    internal override void RestoreData(World world, SparseStores stores, bool preserveProgress)
    {
        SparseStore<T> target = stores.GetOrCreate<T>();
        if (target.Values.Length < Count)
        {
            Array.Resize(ref target.Values, Count);
            Array.Resize(ref target.Entities, Count);
            if (target.Changes is { } tracked) tracked[0].Grow(Count, ComponentType<T>.TracksAddedType);
        }
        Entities.AsSpan(0, Count).CopyTo(target.Entities);
        if (ComponentType<T>.HasValueOperationsType)
            for (int row = 0; row < Count; row++) target.Values[row] = ComponentType<T>.CloneValue(world, Values[row]);
        else Values.AsSpan(0, Count).CopyTo(target.Values);
        target.Count = Count;
        for (int row = 0; row < Count; row++)
        {
            int id = Entities[row].Id;
            int attachment = stores.Attach(id, ComponentType<T>.TypeId);
            target.EnsureCell(id) = new() { RowPlusOne = row + 1, AttachmentPlusOne = attachment };
        }
        if (Changes is { } changes)
        {
            if (preserveProgress) changes[0].CopyTo(ref target.Changes![0], Count);
            else for (int row = 0; row < Count; row++) target.Changes![0].Stamp(row, world.Version, true, ComponentType<T>.TracksAddedType);
        }
    }

    internal override void ReleaseData(World values, bool keepCapacity = false)
    {
        if (ComponentType<T>.HasValueOperationsType)
            for (int row = 0; row < Count; row++) ComponentType<T>.ReleaseValue(values, ref Values[row]);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Values, 0, Count);
        Count = 0;
        if (!keepCapacity) { Values = []; Entities = []; Pages = []; Changes = null; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Get(int entityId, ulong writeVersion = 0)
    {
        ref SparseCell cell = ref Find(entityId);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0)
            throw new ArgumentException("Entity does not have this sparse component.");
        if (writeVersion != 0) Changes![0].Stamp(cell.RowPlusOne - 1, writeVersion, false, ComponentType<T>.TracksAddedType);
        return ref Values[cell.RowPlusOne - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Get(Entity entity, ulong writeVersion = 0)
    {
        ref SparseCell cell = ref Find(entity.Id);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0
            || Entities[cell.RowPlusOne - 1].Generation != entity.Generation)
            throw new ArgumentException("Entity does not have this sparse component.");
        int row = cell.RowPlusOne - 1;
        if (writeVersion != 0) Changes![0].Stamp(row, writeVersion, false, ComponentType<T>.TracksAddedType);
        return ref Values[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(World world, SparseStores stores, Entity entity, in T value)
    {
        ref SparseCell cell = ref Find(entity.Id);
        if (!Unsafe.IsNullRef(ref cell) && cell.RowPlusOne != 0)
        {
            if (ComponentType<T>.HasValueOperationsType)
                ComponentType<T>.ReleaseValue(world, ref Values[cell.RowPlusOne - 1]);
            Values[cell.RowPlusOne - 1] = value;
            if (ComponentType<T>.TracksWritesType)
                Changes![0].Stamp(cell.RowPlusOne - 1, world.NextVersion(), false, ComponentType<T>.TracksAddedType);
        }
        else Add(world, stores, entity, value);
    }

    private void Add(World world, SparseStores stores, Entity entity, in T value)
    {
        world.RequireStructure();
        if (Count == Values.Length)
        {
            Array.Resize(ref Values, Values.Length * 2);
            Array.Resize(ref Entities, Values.Length);
            if (Changes is { } changes) changes[0].Grow(Values.Length, ComponentType<T>.TracksAddedType);
        }
        int attachment = stores.Attach(entity.Id, ComponentType<T>.TypeId);
        ref SparseCell cell = ref EnsureCell(entity.Id);
        Entities[Count] = entity;
        Values[Count] = value;
        if (ComponentType<T>.TracksWritesType) Changes![0].Stamp(Count, world.NextVersion(), true, ComponentType<T>.TracksAddedType);
        cell = new() { RowPlusOne = ++Count, AttachmentPlusOne = attachment };
    }

    internal bool Remove(World world, SparseStores stores, Entity entity)
    {
        ref SparseCell cell = ref Find(entity.Id);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0) return false;
        ulong version = ComponentType<T>.TracksWritesType || ComponentType<T>.KeepsRemovedType ? world.NextVersion() : 0;
        if (ComponentType<T>.TracksWritesType) Changes![0].LastWrite = Math.Max(Changes[0].LastWrite, version);
        if (ComponentType<T>.KeepsRemovedType)
            ((IRemovedValue<T>)ComponentType<T>.Value.RemovedValues!).Capture(world, entity, Values[cell.RowPlusOne - 1], version);
        else if (ComponentType<T>.HasValueOperationsType)
            ComponentType<T>.ReleaseValue(world, ref Values[cell.RowPlusOne - 1]);
        stores.Detach(entity.Id, cell.AttachmentPlusOne);
        RemoveRow(ref cell);
        return true;
    }

    internal override void RemoveKnown(World world, int entityId)
    {
        ref SparseCell cell = ref Find(entityId);
        if (ComponentType<T>.TracksWritesType)
            Changes![0].LastWrite = Math.Max(Changes[0].LastWrite, world.NextVersion());
        if (ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(world, ref Values[cell.RowPlusOne - 1]);
        RemoveRow(ref cell);
    }

    internal override void RemoveForDestroy(World world, Entity entity)
    {
        ref SparseCell cell = ref Find(entity.Id);
        ulong version = ComponentType<T>.TracksWritesType || ComponentType<T>.KeepsRemovedType ? world.NextVersion() : 0;
        if (ComponentType<T>.TracksWritesType) Changes![0].LastWrite = Math.Max(Changes[0].LastWrite, version);
        if (ComponentType<T>.KeepsRemovedType)
            ((IRemovedValue<T>)ComponentType<T>.Value.RemovedValues!).Capture(world, entity, Values[cell.RowPlusOne - 1], version);
        else if (ComponentType<T>.HasValueOperationsType)
            ComponentType<T>.ReleaseValue(world, ref Values[cell.RowPlusOne - 1]);
        RemoveRow(ref cell);
    }

    internal override bool HasChanged(int entityId, ulong since, bool added)
    {
        ref SparseCell cell = ref Find(entityId);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0) return false;
        int row = cell.RowPlusOne - 1;
        return Changes![0].Contains(row, since, added, ComponentType<T>.TracksAddedType);
    }

    internal override void Clone(World world, SparseStores stores, int sourceId, Entity target)
        => Set(world, stores, target, ComponentType<T>.CloneValue(world, Get(sourceId)));

    internal override void CloneTo(World world, int sourceId, Entity target, in EntityMap entities)
    {
        T value = ComponentType<T>.CloneValue(world, Get(sourceId));
        EntityFields<T>.Operations?.Remap(ref value, entities);
        world.Set(target, value);
    }

    private void RemoveRow(ref SparseCell cell)
    {
        int row = cell.RowPlusOne - 1;
        int last = --Count;
        if (Changes is { } changes)
        {
            if (row != last) changes[0].Copy(row, ref changes[0], last, ComponentType<T>.TracksAddedType);
            changes[0].ClearRow(last, ComponentType<T>.TracksAddedType);
        }
        if (row != last)
        {
            Entity moved = Entities[last];
            Entities[row] = moved;
            Values[row] = Values[last];
            Find(moved.Id).RowPlusOne = row + 1;
        }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Values[last] = default;
        cell = default;
    }

    internal override void Clear(World world)
    {
        if (ComponentType<T>.HasValueOperationsType)
            for (int row = 0; row < Count; row++) ComponentType<T>.ReleaseValue(world, ref Values[row]);
        for (int i = 0; i < Count; i++) Find(Entities[i].Id) = default;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Values, 0, Count);
        Count = 0;
        if (Changes is { } changes)
        {
            Array.Clear(changes[0].Rows);
            Array.Clear(changes[0].Words);
            Array.Clear(changes[0].Blocks);
            changes[0].LastWrite = 0;
        }
    }
}
