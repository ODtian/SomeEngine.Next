using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Sparse;

internal struct SparseAttachment
{
    internal int TypeId;
    internal int Previous;
    internal int Next;
}

internal sealed class SparseStores(World world, int entityCapacity)
{
    internal SparseStore?[] Types = [];
    internal int[] First = new int[entityCapacity];
    private SparseAttachment[] _attachments = new SparseAttachment[16];
    private int _used;
    private int _free;
    internal bool HasRetainingTypes;

    internal SparseStore<T> GetOrCreate<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        if ((uint)type < (uint)Types.Length && Types[type] is { } found) return (SparseStore<T>)found;
        if (type >= Types.Length) Array.Resize(ref Types, Math.Max(type + 1, Math.Max(16, Types.Length * 2)));
        var store = new SparseStore<T>();
        HasRetainingTypes |= store.IsCleanup || store.KeepsRemoved;
        Types[type] = store;
        return store;
    }

    internal SparseStore<T>? Find<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)Types.Length ? (SparseStore<T>?)Types[type] : null;
    }

    internal void ReserveEntities(int capacity) => Array.Resize(ref First, capacity);

    internal int Attach(int entityId, int typeId)
    {
        int index;
        if (_free != 0)
        {
            index = _free;
            _free = _attachments[index].Next;
        }
        else
        {
            index = ++_used;
            if (index == _attachments.Length) Array.Resize(ref _attachments, _attachments.Length * 2);
        }
        ref int first = ref First[entityId];
        if (first != 0) _attachments[first].Previous = index;
        _attachments[index] = new() { TypeId = typeId, Next = first };
        first = index;
        return index;
    }

    internal void Detach(int entityId, int index)
    {
        ref SparseAttachment entry = ref _attachments[index];
        if (entry.Previous == 0) First[entityId] = entry.Next;
        else _attachments[entry.Previous].Next = entry.Next;
        if (entry.Next != 0) _attachments[entry.Next].Previous = entry.Previous;
        entry.Next = _free;
        _free = index;
    }

    internal void RemoveAll(int entityId)
    {
        int first = First[entityId];
        if (first == 0) return;
        int index = first;
        int last;
        do
        {
            ref SparseAttachment entry = ref _attachments[index];
            Types[entry.TypeId]!.RemoveKnown(world, entityId);
            last = index;
            index = entry.Next;
        } while (index != 0);
        _attachments[last].Next = _free;
        _free = first;
        First[entityId] = 0;
    }

    internal bool HasRetained(int entityId)
    {
        if (!HasRetainingTypes) return false;
        for (int index = First[entityId]; index != 0; index = _attachments[index].Next)
        {
            SparseStore store = Types[_attachments[index].TypeId]!;
            if (store.IsCleanup || store.KeepsRemoved) return true;
        }
        return false;
    }

    internal bool HasEntityReferences(int entityId)
    {
        for (int index = First[entityId]; index != 0; index = _attachments[index].Next)
            if (Types[_attachments[index].TypeId]!.HasEntityReferences) return true;
        return false;
    }

    internal void RemoveForDestroy(World world, Entities.Entity entity)
    {
        for (int index = First[entity.Id]; index != 0;)
        {
            SparseAttachment attachment = _attachments[index];
            SparseStore store = Types[attachment.TypeId]!;
            if (!store.IsCleanup)
            {
                store.RemoveForDestroy(world, entity);
                Detach(entity.Id, index);
            }
            index = attachment.Next;
        }
    }

    internal void Clear(int usedEntities)
    {
        foreach (SparseStore? store in Types) store?.Clear(world);
        Array.Clear(First, 0, usedEntities + 1);
        _used = _free = 0;
    }

    internal void ReleaseData()
    {
        foreach (SparseStore? store in Types) store?.ReleaseData(world);
    }

    internal void Clone(World world, int sourceId, Entities.Entity target)
    {
        for (int index = First[sourceId]; index != 0;)
        {
            SparseAttachment attachment = _attachments[index];
            Types[attachment.TypeId]!.Clone(world, this, sourceId, target);
            index = attachment.Next;
        }
    }

    internal void CloneTo(World targetWorld, int sourceId, Entities.Entity target, in EntityMap entities)
    {
        for (int index = First[sourceId]; index != 0;)
        {
            SparseAttachment attachment = _attachments[index];
            Types[attachment.TypeId]!.CloneTo(targetWorld, sourceId, target, entities);
            index = attachment.Next;
        }
    }
}
