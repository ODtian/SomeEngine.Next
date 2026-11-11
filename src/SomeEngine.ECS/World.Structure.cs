using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add<T>(Entity entity, in T value) where T : struct
    {
        if (Execution is not null) { AddWithExecution(entity, value); return; }
        AddCore(entity, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddCore<T>(Entity entity, in T value) where T : struct
    {
        ComponentValues.Bind<T>();
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        int type = ComponentType<T>.TypeId;
        ColumnEntry[] columns = record.Chunk!.Columns;
        bool present = (uint)type < (uint)columns.Length && columns[type].Has(record.Row);
        if (present) throw new InvalidOperationException("The entity already has this component.");
        using MembershipEdit membership = BeginMembershipEdit(entity);
        if (ComponentType<T>.IsIndexedType && ActiveIndex<T>() is { } index) { index.Set(entity, value); return; }
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping) { grouping.Set(entity, value); return; }
        if (ComponentType<T>.IsSharedType) { SetSharedValue(entity, value); return; }
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed add entry.");
        if ((uint)type < (uint)columns.Length && columns[type].ColumnPlusOne != 0)
        {
            record.Chunk.RestorePresence(type, record.Row);
            if (!ComponentType<T>.IsTagType) ColumnEntry.At<T>(ref columns[type], record.Row) = value;
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Bind(entity, record.Chunk, record.Row);
            if (ComponentType<T>.TracksWritesType) Stamp<T>(record.Chunk, record.Row, NextVersion(), true);
        }
        else AddKnown(entity, ref record, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddKnown<T>(Entity entity, ref EntityRecord record, in T value, ulong version = 0) where T : struct
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        RequireStructure();
        Chunk source = record.Chunk!;
        Archetype archetype = source.Archetype;
        int type = ComponentType<T>.TypeId;
        if ((uint)type < (uint)source.Columns.Length && source.Columns[type].ColumnPlusOne != 0)
        {
            bool present = !(ComponentType<T>.TracksAddedType || ComponentType<T>.HasValueOperationsType) || source.Columns[type].Has(record.Row);
            bool added = ComponentType<T>.TracksAddedType && !present;
            if (ComponentType<T>.HasValueOperationsType && present)
                ComponentType<T>.ReleaseValue(this, ref ColumnEntry.At<T>(ref source.Columns[type], record.Row));
            source.RestorePresence(type, record.Row);
            if (!ComponentType<T>.IsTagType)
                ColumnEntry.At<T>(ref source.Columns[type], record.Row) = value;
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Bind(entity, source, record.Row);
            if (ComponentType<T>.TracksWritesType) Stamp<T>(source, record.Row, version != 0 ? version : NextVersion(), added);
            return;
        }
        Transition edge = GetTransition(source, type, true, record.Row);
        MoveKnown(entity, ref record, source, edge);
        if (!ComponentType<T>.IsTagType)
            ColumnEntry.At<T>(ref edge.Target.Columns[type], record.Row) = value;
        if (ComponentType<T>.TracksWritesType) Stamp<T>(edge.Target, record.Row, version != 0 ? version : NextVersion(), true);
    }

    public void Remove<T>(Entity entity) where T : struct
    {
        if (Execution is not null) { RemoveWithExecution<T>(entity); return; }
        RemoveCore<T>(entity);
    }

    public void Remove<T>(ReadOnlySpan<Entity> entities) where T : struct
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        if (ComponentType<T>.IsControlledRemovalType) throw new InvalidOperationException("Use the component's typed removal entry.");
        foreach (Entity entity in entities)
        {
            ref EntityRecord record = ref Resolve(entity);
            RemoveValueKnown<T>(entity, ref record);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveCore<T>(Entity entity) where T : struct
    {
        if (ComponentType<T>.IsControlledRemovalType) throw new InvalidOperationException("Use the component's typed removal entry.");
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        int type = ComponentType<T>.TypeId;
        ColumnEntry[] columns = record.Chunk!.Columns;
        if ((uint)type >= (uint)columns.Length || !columns[type].Has(record.Row))
            throw new InvalidOperationException("The entity requires this component before removing it.");
        RemoveValueKnown<T>(entity, ref record);
    }

    internal void RemoveValue<T>(Entity entity, ulong version = 0) where T : struct
    {
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        RemoveValueKnown<T>(entity, ref record, version);
    }

    internal void RemoveValueKnown<T>(Entity entity, ref EntityRecord record, ulong version = 0) where T : struct
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        Chunk source = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)source.Columns.Length || !source.Columns[type].Has(record.Row)) return;
        if (ComponentType<T>.IsIndexedType) ActiveIndex<T>()?.Remove(entity.Id);
        if (ComponentType<T>.KeepsRemovedType)
        {
            ComponentType<T>.Value.RemovedValues!.Capture(this, entity, ref record, version != 0 ? version : NextVersion());
            source = record.Chunk!;
        }
        else if (ComponentType<T>.HasValueOperationsType && !ComponentType<T>.IsSharedType)
            ComponentType<T>.ReleaseValue(this, ref ColumnEntry.At<T>(ref source.Columns[type], record.Row));
        if (ComponentType<T>.IsSharedType) RemoveSharedMember(entity, source, type, record.Row);
        if (ComponentType<T>.TracksWritesType)
        {
            ref ComponentChanges changes = ref source.Changes![source.Columns[type].ColumnPlusOne - 1];
            changes.LastWrite = Math.Max(changes.LastWrite, version != 0 ? version : NextVersion());
            changes.ClearRow(record.Row, ComponentType<T>.TracksAddedType);
        }
        if (_retainRemovedColumns)
        {
            ulong[] words = EnsurePresence(source, type);
            source.RemoveEnabled(type, record.Row);
            words[record.Row >> 6] &= ~(1UL << (record.Row & 63));
            if (!ComponentType<T>.IsTagType && !ComponentType<T>.IsSharedType) ColumnEntry.At<T>(ref source.Columns[type], record.Row) = default;
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Remove(entity);
            if (ComponentType<T>.IsCleanupType) ReleaseAfterCleanup(entity, ref record);
            return;
        }
        Transition edge = GetTransition(source, type, false, record.Row);
        MoveKnown(entity, ref record, source, edge);
        if (ComponentType<T>.IsCleanupType) ReleaseAfterCleanup(entity, ref record);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Transition GetTransition(Chunk source, int type, bool add, int row)
    {
        Transition?[] edges = add ? source.Archetype.Additions : source.Archetype.Removals;
        if ((uint)type < (uint)edges.Length && edges[type] is { } found) return found;
        return BuildTransition(source, type, add, row);
    }

    private Transition BuildTransition(Chunk sourceChunk, int type, bool add, int row)
    {
        Archetype source = sourceChunk.Archetype;
        int[] original = source.Types.Types;
        int[] changed = new int[original.Length + (add ? 1 : -1)];
        if (add)
        {
            original.CopyTo(changed, 0);
            changed[^1] = type;
        }
        else
        {
            int position = Array.BinarySearch(original, type);
            original.AsSpan(0, position).CopyTo(changed);
            original.AsSpan(position + 1).CopyTo(changed.AsSpan(position));
        }
        TypeSet targetTypes = new(false, changed);
        Archetype target = FindWithGroups(targetTypes, sourceChunk, row);
        Archetype shared = add ? source : target;
        var edge = new Transition(target.WritableChunk, shared.Pod, shared.Managed,
            add ? null : ComponentTypes.Get(type));
        ref Transition?[] edges = ref (add ? ref source.Additions : ref source.Removals);
        if (type >= edges.Length) Array.Resize(ref edges, Math.Max(type + 1, Math.Max(16, edges.Length * 2)));
        edges[type] = edge;
        return edge;
    }

    private void MoveWithFeatures(Entity entity, ref EntityRecord record, Chunk source, Chunk target,
        PodColumn[] pod, ComponentType[] managed, ComponentType? removedType)
    {
        int sourceRow = record.Row;
        int targetRow = target.Count++;
        target.Entities[targetRow] = entity;
        ColumnEntry[] sourceColumns = source.Columns;
        ColumnEntry[] targetColumns = target.Columns;
        int last = --source.Count;
        if (sourceRow == last)
        {
            foreach (PodColumn description in pod)
                Chunk.CopyBytes(sourceColumns[description.TypeId].Rows + (nint)sourceRow * description.Size,
                    targetColumns[description.TypeId].Rows + (nint)targetRow * description.Size, description.Size);
        }
        else
        {
            foreach (PodColumn description in pod)
            {
                nint sourceBase = sourceColumns[description.TypeId].Rows;
                nint sourceAddress = sourceBase + (nint)sourceRow * description.Size;
                Chunk.CopyBytes(sourceAddress, targetColumns[description.TypeId].Rows + (nint)targetRow * description.Size,
                    description.Size);
                Chunk.CopyBytes(sourceBase + (nint)last * description.Size, sourceAddress, description.Size);
            }
            if (removedType is { IsTag: false, IsShared: false, ContainsReferences: false } removed)
            {
                nint values = sourceColumns[removed.Id].Rows;
                Chunk.CopyBytes(values + (nint)last * removed.Size, values + (nint)sourceRow * removed.Size, removed.Size);
            }
        }
        foreach (ComponentType type in managed)
            type.Copy(ref sourceColumns[type.Id], sourceRow, ref targetColumns[type.Id], targetRow);
        foreach (ComponentType type in source.Archetype.Shared)
        {
            int[] ids = Unsafe.As<int[]>(sourceColumns[type.Id].Values!);
            if ((uint)type.Id < (uint)targetColumns.Length && targetColumns[type.Id].ColumnPlusOne != 0)
                Unsafe.As<int[]>(targetColumns[type.Id].Values!)[targetRow] = ids[sourceRow];
            if (sourceRow != last) ids[sourceRow] = ids[last];
        }
        if (source.Changes is { } changes)
        {
            foreach (ComponentType type in source.Archetype.Tracked)
                if ((uint)type.Id < (uint)targetColumns.Length && targetColumns[type.Id].ColumnPlusOne != 0)
                    target.Changes![targetColumns[type.Id].ColumnPlusOne - 1].Copy(targetRow,
                        ref changes[sourceColumns[type.Id].ColumnPlusOne - 1], sourceRow, type.TracksAdded);
            source.MoveChanges(last, sourceRow);
        }
        TransferPresence(source, sourceRow, target, targetRow, last);
        if (sourceRow != last)
        {
            Entity moved = source.Entities[last];
            source.Entities[sourceRow] = moved;
            Records[moved.Id].Row = sourceRow;
            foreach (ComponentType type in source.Archetype.Managed)
                type.Copy(ref sourceColumns[type.Id], last, ref sourceColumns[type.Id], sourceRow);
            if (source.Archetype.Cached is { } cached)
                foreach (CachedComponent data in cached) data.Bind(moved, source, sourceRow);
        }
        foreach (ComponentType type in source.Archetype.Managed) type.Clear(ref sourceColumns[type.Id], last);
        record.Chunk = target;
        record.Row = targetRow;
        if (target.Archetype.Cached is { } targetCached)
            foreach (CachedComponent data in targetCached) data.Bind(entity, target, targetRow);
        if (removedType is not null && source.Archetype.Cached is { } sourceCached)
            foreach (CachedComponent data in sourceCached)
                if (data.TypeId == removedType.Id) data.Remove(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy(Entity entity)
    {
        if (Execution is not null) { DestroyWithExecution(entity); return; }
        DestroyCore(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DestroyCore(Entity entity)
    {
        RequireStructure();
        if (_lifecycleCount != 0 || HasDestroyedReaders) { DestroyObserved(entity, false); return; }
        ref EntityRecord record = ref Resolve(entity);
        if (HasDestroyed(in record)) throw new InvalidOperationException("Destroy an active entity.");
        DestroyComponents(entity, ref record);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DestroyComponents(Entity entity, ref EntityRecord record)
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        if (RetainsValues(entity, in record))
        {
            DestroyRetained(entity, ref record);
            return;
        }
        ReleaseEntity(entity, ref record);
    }

    private bool RetainsValues(Entity entity, in EntityRecord record)
    {
        Chunk chunk = record.Chunk!;
        if (chunk.Archetype.RetainsOnDestroy)
            foreach (ComponentType type in chunk.Archetype.Components)
                if ((type.IsCleanup || type.KeepsRemoved) && chunk.Columns[type.Id].Has(record.Row)) return true;
        return _sparse?.HasRetained(entity.Id) ?? false;
    }

    private void ReleaseEntity(Entity entity, ref EntityRecord record)
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        Chunk chunk = record.Chunk!;
        MarkRemovedValues(chunk, record.Row);
        RemoveIndexMembers(entity, chunk, record.Row);
        foreach (ComponentType type in chunk.Archetype.OwnedValues)
            if (chunk.Columns[type.Id].Has(record.Row)) type.Release(this, ref chunk.Columns[type.Id], record.Row);
        if (record.Chunk!.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.Remove(entity);
        _sparse?.RemoveAll(entity.Id);
        record.Chunk!.RemoveRow(record.Row, Records);
        record = new(null, _free, record.Generation + 1);
        _free = entity.Id;
        _count--;
    }

    public void Destroy(ReadOnlySpan<Entity> entities)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        bool validated = false;
        if (_lifecycleCount != 0 || HasDestroyedReaders)
        {
            if (CanReleaseObservedBatch(entities))
            {
                PublishDestroyed(entities);
                validated = true;
            }
            else
            {
                PrepareDestroyColumns(entities);
                foreach (Entity entity in entities)
                    if (IsAliveCore(entity) && !HasDestroyed(in Records[entity.Id])) DestroyObserved(entity, false);
                return;
            }
        }
        if (!validated && (_hasRetainedTypes || (_sparse?.HasRetainingTypes ?? false)))
            foreach (Entity entity in entities)
                if (RetainsValues(entity, in Resolve(entity)))
                {
                    foreach (Entity item in entities) Destroy(item);
                    return;
                }
        // Row's sign bit is temporary batch membership while this structural operation is exclusive.
        if (_membershipQueries.Count != 0) PublishMembershipBatch(entities, false);
        if (validated)
            foreach (Entity entity in entities) Records[entity.Id].Row |= int.MinValue;
        else foreach (Entity entity in entities) Resolve(entity).Row |= int.MinValue;
        foreach (Entity entity in entities)
        {
            Chunk? chunk = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), entity.Id).Chunk;
            if (chunk is null) continue;
            Archetype archetype = chunk.Archetype;
            if (CanReleaseTail(archetype))
                RemoveMarkedTail(chunk);
            else while (chunk.Count != 0)
            {
                Entity tail = chunk.Entities[chunk.Count - 1];
                if (Records[tail.Id].Row >= 0) break;
                RemoveBatchEntity(tail);
            }
            if (Records[entity.Id].Chunk is not null) RemoveBatchEntity(entity);
        }
        int free = _free;
        ref EntityRecord firstRecord = ref MemoryMarshal.GetArrayDataReference(Records);
        foreach (Entity entity in entities)
        {
            Unsafe.Add(ref firstRecord, entity.Id).Row = free;
            free = entity.Id;
        }
        _free = free;
        _count -= entities.Length;
    }

    private bool CanReleaseObservedBatch(ReadOnlySpan<Entity> entities)
    {
        if (_sparse is not null) return false;
        Archetype? previous = null;
        foreach (Entity entity in entities)
        {
            if (!IsAliveCore(entity)) return false;
            Archetype archetype = Records[entity.Id].Chunk!.Archetype;
            if (archetype == previous) continue;
            if ((archetype.TransferKinds & ~2) != 0 || archetype.RetainsOnDestroy
                || archetype.OwnedValues.Length != 0 || archetype.Indexed.Length != 0) return false;
            for (int i = 0; i < _lifecycleCount; i++)
                if (_lifecycles[i].RequiresIndividualDestroy(archetype)) return false;
            previous = archetype;
        }
        return true;
    }

    private void RemoveMarkedTail(Chunk chunk)
    {
        int end = chunk.Count, start = end;
        while (start > 0 && Records[chunk.Entities[start - 1].Id].Row < 0) start--;
        if (start == end) return;
        MarkRemovedValues(chunk, start);
        Archetype archetype = chunk.Archetype;
        foreach (ComponentType type in archetype.OwnedValues)
            type.ReleaseRows(this, ref chunk.Columns[type.Id], start, end - start);
        foreach (ComponentType type in archetype.Managed)
            Array.Clear((Array)chunk.Columns[type.Id].Values!, start, end - start);
        foreach (ComponentType type in archetype.Tracked)
            chunk.Changes![chunk.Columns[type.Id].ColumnPlusOne - 1].ClearTail(start, end);
        ReadOnlySpan<Entity> removed = chunk.Entities.AsSpan(start, end - start);
        if (chunk.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.RemoveRange(removed);
        if (_sparse is { } sparse)
            foreach (Entity entity in removed) sparse.RemoveAll(entity.Id);
        ref EntityRecord firstRecord = ref MemoryMarshal.GetArrayDataReference(Records);
        foreach (Entity entity in removed)
        {
            ref EntityRecord record = ref Unsafe.Add(ref firstRecord, entity.Id);
            record.Chunk = null;
            record.Generation++;
        }
        chunk.Count = start;
        if (ChunkCapacity != 0) chunk.Archetype.MakeAvailable(chunk);
    }

    private static bool CanReleaseTail(Archetype archetype)
    {
        if ((archetype.TransferKinds & ~(1 | 2 | 8)) != 0 || archetype.Indexed.Length != 0) return false;
        foreach (ComponentType type in archetype.OwnedValues)
            if (!type.HasColumnRelease) return false;
        return true;
    }

    private void RemoveBatchEntity(Entity entity)
    {
        ref EntityRecord record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), entity.Id);
        Chunk chunk = record.Chunk!;
        MarkRemovedValues(chunk, record.Row & int.MaxValue);
        RemoveIndexMembers(entity, chunk, record.Row & int.MaxValue);
        foreach (ComponentType type in chunk.Archetype.OwnedValues)
            if (chunk.Columns[type.Id].Has(record.Row & int.MaxValue))
                type.Release(this, ref chunk.Columns[type.Id], record.Row & int.MaxValue);
        if (chunk.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.Remove(entity);
        _sparse?.RemoveAll(entity.Id);
        chunk.RemoveRow(record.Row & int.MaxValue, Records, markedRows: true);
        record.Chunk = null;
        record.Generation++;
    }
}
