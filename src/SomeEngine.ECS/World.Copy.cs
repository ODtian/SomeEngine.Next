using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    /// <summary>Copies ordinary component values into an existing entity, preserving its identity.</summary>
    public void Copy(Entity source, Entity target)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        ref EntityRecord sourceRecord = ref Resolve(source);
        ref EntityRecord targetRecord = ref Resolve(target);
        if (source == target) return;
        if (HasDestroyed(in sourceRecord) || HasDestroyed(in targetRecord))
            throw new InvalidOperationException("Copy component values between active entities.");
        foreach (ComponentType type in sourceRecord.Chunk!.Archetype.Components)
            if (type.InitializesEntity || type.IsComputed)
                throw new InvalidOperationException("Copy relationship values through their typed entry.");
        foreach (ComponentType type in targetRecord.Chunk!.Archetype.Components)
            if (type.InitializesEntity || type.IsComputed)
                throw new InvalidOperationException("Copy relationship values through their typed entry.");
        Archetype destination = CopyTarget(this, source);
        using BatchVersionScope version = BeginBatchVersion(0);
        using MembershipEdit membership = BeginMembershipEdit(target);
        Chunk before = targetRecord.Chunk!;
        MarkRemovedValues(before, targetRecord.Row);
        RemoveIndexMembers(target, before, targetRecord.Row);
        foreach (ComponentType type in before.Archetype.OwnedValues)
            if (before.Columns[type.Id].Has(targetRecord.Row)) type.Release(this, ref before.Columns[type.Id], targetRecord.Row);
        if (before.Archetype.Cached is { } cached)
            foreach (CachedComponent values in cached) values.Remove(target);
        _sparse?.RemoveAll(target.Id);
        if (before.Archetype != destination)
        {
            PodColumn[] pod = before.Archetype.Pod.Where(column => destination.Types.Bits.Has(column.TypeId)).ToArray();
            ComponentType[] managed = before.Archetype.Managed.Where(type => destination.Types.Bits.Has(type.Id)).ToArray();
            MoveKnown(target, ref targetRecord, before, destination.WritableChunk, pod, managed, null);
        }
        StampCreated(targetRecord.Chunk!, targetRecord.Row, 1);
        Span<Entity> sources = stackalloc Entity[1] { source };
        Span<Entity> targets = stackalloc Entity[1] { target };
        _copies ??= new();
        var entities = new EntityMap(sources, targets);
        CopyValuesTo(this, sources, targets, entities);
        CompleteCopies(targets, publishMembership: false);
    }

    public Entity Clone(Entity source)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        ref EntityRecord record = ref Resolve(source);
        Chunk chunk = record.Chunk!;
        if (chunk.Archetype.HasEntityReferences || _sparse?.HasEntityReferences(source.Id) == true)
            return CloneEntityReferences(source);
        int sourceRow = record.Row;
        Archetype targetArchetype = chunk.Archetype;
        if (targetArchetype.Computed.Length != 0)
            targetArchetype = FindWithGroups(new TypeSet(false, targetArchetype.Components.Where(t => !t.IsComputed).Select(t => t.Id).ToArray()), chunk, sourceRow);
        Chunk targetChunk = targetArchetype.WritableChunk;
        Entity target = CreateKnown(ref targetChunk);
        using MembershipEdit membership = BeginMembershipEdit(target, created: true);
        int targetRow = targetChunk.Count - 1;
        if (targetArchetype.CanClonePodAndBuffers && (chunk.Archetype.TransferKinds & 4) == 0)
        {
            foreach (PodColumn column in targetArchetype.Pod)
                Chunk.CopyBytes(chunk.Columns[column.TypeId].Rows + (nint)sourceRow * column.Size,
                    targetChunk.Columns[column.TypeId].Rows + (nint)targetRow * column.Size, column.Size);
            foreach (ComponentType type in targetArchetype.Managed)
                type.Clone(this, ref chunk.Columns[type.Id], sourceRow, ref targetChunk.Columns[type.Id], targetRow);
        }
        else
        {
            foreach (ComponentType type in chunk.Archetype.Components)
            {
                if (type.IsComputed) continue;
                ref ColumnEntry column = ref chunk.Columns[type.Id];
                ref ColumnEntry targetColumn = ref targetChunk.Columns[type.Id];
                if (!column.Has(sourceRow))
                {
                    EnsurePresence(targetChunk, type.Id)[targetRow >> 6] &= ~(1UL << (targetRow & 63));
                    if (type.TracksWrites) targetChunk.Changes![targetColumn.ColumnPlusOne - 1].ClearRow(targetRow, type.TracksAdded);
                    if ((uint)type.Id < (uint)_cachedByType.Length) _cachedByType[type.Id]?.Remove(target);
                    continue;
                }
                if (!type.ContainsReferences && !type.HasValueOperations && !type.IsShared)
                {
                    if (!type.IsTag)
                        Chunk.CopyBytes(column.Rows + (nint)sourceRow * type.Size,
                            targetColumn.Rows + (nint)targetRow * type.Size, type.Size);
                }
                else type.Clone(this, ref column, sourceRow, ref targetColumn, targetRow);
                if (!chunk.IsEnabled(type.Id, sourceRow)) SetEnabled(targetChunk, type.Id, targetRow, false);
                if (type.IsShared) CopySharedMember(target, targetChunk, type.Id, targetRow);
                if (type.IsIndexed && (uint)type.Id < (uint)_valueIndexes.Length) _valueIndexes[type.Id]?.AddFromEntity(target, in targetColumn, targetRow);
            }
        }
        if (chunk.Archetype.Groupable.Length != 0) ArrangeCreatedGroups(target);
        _sparse?.Clone(this, source.Id, target);
        foreach (ComponentType type in targetArchetype.Initializers)
            if (Records[target.Id].Chunk!.Columns[type.Id].Has(Records[target.Id].Row)) type.InitializeEntity(this, target);
        return target;
    }

    private Entity CloneEntityReferences(Entity source)
    {
        Span<Entity> sources = stackalloc Entity[1] { source };
        Span<Entity> targets = stackalloc Entity[1];
        Clone(sources, targets);
        return targets[0];
    }
}
