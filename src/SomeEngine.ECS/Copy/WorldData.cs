using SomeEngine.Job;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal interface IWorldDataService
{
    void CompleteData();
    WorldServiceData CopyData(bool preserveProgress, WorldServiceData? target = null);
}

internal abstract class WorldServiceData : IDisposable
{
    internal abstract void Restore(World world);
    internal abstract void Clear(bool keepCapacity);
    public void Dispose() => Clear(false);
}

internal readonly record struct WorldEvents(DestroyedEntity[] Values, ulong Through, EventReaderPosition[] ReaderPositions,
    int ValueCount, int ReaderCount)
{
    internal WorldEvents(DestroyedEntity[] values, ulong through, EventReaderPosition[] readers)
        : this(values, through, readers, values.Length, readers.Length) { }
}

public sealed partial class World
{
    internal int FirstFreeEntity => _free;
    internal ulong DestroyedSequence => _destroyedLog?.Through ?? 0;
    internal SharedValues?[] SharedTypes { get => _shared; set => _shared = value; }
    internal SparseStores? SparseTypes { get => _sparse; set => _sparse = value; }
    internal ref ResourceEntries ResourceValues => ref _resources;
    internal ReadOnlySpan<IWorldLifecycle> LifecycleServices => _lifecycles.AsSpan(0, _lifecycleCount);

    internal WorldAccess OpenValues(ReadOnlySpan<WorldValueAccess> values)
    {
        Execution?.EnterValues(values);
        return new(Execution);
    }

    internal void RestoreEntityIds(ReadOnlySpan<int> generations, ReadOnlySpan<int> freeIds, int count, ulong version)
    {
        int savedUsed = generations.Length - 1;
        ReserveEntities(savedUsed);
        int used = Math.Max(Used, savedUsed);
        for (int id = 1; id <= savedUsed; id++)
            Records[id] = new(null, 0, Math.Max(Records[id].Generation, generations[id]));
        int next = 0;
        for (int id = used; id > savedUsed; id--) { Records[id].Row = next; next = id; }
        for (int i = freeIds.Length - 1; i >= 0; i--) { Records[freeIds[i]].Row = next; next = freeIds[i]; }
        _free = next;
        Used = used;
        _count = count;
        _version = version;
    }

    internal void RestoreEntityIds(ReadOnlySpan<EntityRecord> saved, int freeHead, int freeTail, int count, ulong version)
    {
        int savedUsed = saved.Length - 1;
        ReserveEntities(savedUsed);
        int used = Math.Max(Used, savedUsed);
        for (int id = 1; id <= savedUsed; id++)
            Records[id] = new(null, saved[id].Row, Math.Max(Records[id].Generation, saved[id].Generation));
        int next = 0;
        for (int id = used; id > savedUsed; id--) { Records[id].Row = next; next = id; }
        if (freeTail != 0) Records[freeTail].Row = next;
        _free = freeHead != 0 ? freeHead : next;
        Used = used;
        _count = count;
        _version = version;
    }

    internal Archetype RestoreArchetype(TypeSet types, ReadOnlySpan<int> groupKeys)
        => groupKeys.Length == 0 ? Find(types) : GetGroupTables(types).Get(groupKeys);

    internal void RebuildDataGroups()
    {
        if (_groupings.Length == 0) return;
        var entities = new Entity[_count];
        int next = 0;
        for (int i = 0; i < ArchetypeCount; i++)
        {
            Archetypes[i].ResetWritableChunks();
            foreach (Chunk chunk in Archetypes[i].AllChunks)
            {
                chunk.Entities.AsSpan(0, chunk.Count).CopyTo(entities.AsSpan(next));
                next += chunk.Count;
            }
        }
        var targets = new Archetype[entities.Length];
        var arrivals = new Dictionary<Archetype, int>();
        var groupKeys = new int[_groupings.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            ref EntityRecord record = ref Records[entities[i].Id];
            Chunk chunk = record.Chunk!;
            if (!chunk.Archetype.Types.Bits.Intersects(_groupedTypes))
            { targets[i] = chunk.Archetype; continue; }
            GroupTables tables = GetGroupTables(chunk.Archetype.Types);
            Span<int> keys = groupKeys.AsSpan(0, tables.Groupings.Length);
            for (int group = 0; group < keys.Length; group++)
            {
                ComponentGrouping grouping = tables.Groupings[group];
                keys[group] = chunk.Columns[grouping.Type].Has(record.Row) ? grouping.Read(this, chunk, record.Row) : 0;
            }
            Archetype target = targets[i] = tables.Get(keys);
            if (target != chunk.Archetype) arrivals[target] = arrivals.GetValueOrDefault(target) + 1;
        }
        foreach (var arrival in arrivals)
        {
            int count = 0;
            foreach (Chunk chunk in arrival.Key.AllChunks) count += chunk.Count;
            arrival.Key.Reserve(count + arrival.Value);
        }
        for (int i = 0; i < entities.Length; i++)
        {
            ref EntityRecord record = ref Records[entities[i].Id];
            if (record.Chunk!.Archetype != targets[i]) MoveToGroup(entities[i], ref record, targets[i]);
        }
    }

    internal void RebuildDataIndexes()
    {
        for (int i = 0; i < ArchetypeCount; i++)
        {
            Archetype archetype = Archetypes[i];
            foreach (Chunk chunk in archetype.AllChunks)
            {
                foreach (ComponentType type in archetype.Shared)
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.Columns[type.Id].Has(row)) CopySharedMember(chunk.Entities[row], chunk, type.Id, row);
                foreach (ComponentType type in archetype.Indexed)
                    if ((uint)type.Id < (uint)_valueIndexes.Length && _valueIndexes[type.Id] is { } index)
                        for (int row = 0; row < chunk.Count; row++)
                            if (chunk.Columns[type.Id].Has(row) && chunk.IsEnabled(type.Id, row)) index.AddFromEntity(chunk.Entities[row], in chunk.Columns[type.Id], row);
                if (archetype.Cached is { } cached)
                    foreach (CachedComponent values in cached) values.BindRange(chunk, 0, chunk.Count);
            }
            archetype.ResetWritableChunks();
        }
        QueryVersion++;
        RefreshQueryMatches();
    }

    internal WorldEvents CopyEvents(in WorldEvents target = default)
    {
        if (_destroyedLog is null) return new([], 0, []);
        var data = new EventLogData<DestroyedEntity>
        {
            Values = target.Values ?? [], Readers = target.ReaderPositions ?? [],
        };
        _destroyedLog.CaptureData(data, Execution?.CurrentAccess ?? default);
        return new(data.Values, data.Through, data.Readers, data.Count, data.ReaderCount);
    }

    internal void RestoreEvents(in WorldEvents events)
    {
        DestroyedLog.RestoreData(new EventLogData<DestroyedEntity>
        {
            Values = events.Values, Count = events.ValueCount, Through = events.Through,
            Readers = events.ReaderPositions, ReaderCount = events.ReaderCount,
        }, Execution?.CurrentAccess ?? default, restoreAnonymousReaders: true);
    }
}
