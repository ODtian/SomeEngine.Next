using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public readonly struct Removed<T>(T value, ulong version) : ICleanupComponent, IComponentOperations<Removed<T>>, IEntityReferenceOperations<Removed<T>> where T : struct
{
    internal readonly T PreviousValue = value;
    public T Value => PreviousValue;
    public ulong Version { get; } = version;
    Removed<T> IComponentOperations<Removed<T>>.Clone(World world, in Removed<T> value)
        => new(ComponentValues.Clone(world, value.Value), value.Version);
    void IComponentOperations<Removed<T>>.Release(World world, ref Removed<T> value)
    {
        T old = value.Value;
        ComponentValues.Release(world, ref old);
        value = default;
    }
    void IEntityReferenceOperations<Removed<T>>.Remap(ref Removed<T> value, in EntityMap entities)
    {
        T old = value.Value;
        ComponentValues.Remap(ref old, entities);
        value = new(old, value.Version);
    }

}

public struct Destroyed : ITag;

public sealed partial class World
{
    private ulong _version;
    private void MarkRemovedValues(Chunk chunk, int row)
    {
        ulong version = 0;
        foreach (ComponentType type in chunk.Archetype.Tracked)
            if (chunk.Columns[type.Id].Has(row))
            {
                ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[type.Id].ColumnPlusOne - 1];
                changes.LastWrite = Math.Max(changes.LastWrite, WriteVersion(ref version));
            }
    }
    public ulong Version => _version;
    public ulong AcquireSystemVersion() => AdvanceVersion();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong NextVersion()
        => _usesBatchVersions ? BatchVersion() : NextVersionCore();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextVersionCore()
    {
        if (Execution is null) return ++_version;
        ulong version = Execution.CurrentWriteVersion;
        return version != 0 ? version : AdvanceVersion();
    }

    internal ulong AdvanceVersion() => unchecked((ulong)Interlocked.Increment(ref Unsafe.As<ulong, long>(ref _version)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong WriteVersion(ref ulong version) => version == 0 ? version = NextVersion() : version;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Stamp<T>(Chunk chunk, int row, ulong version, bool added = false) where T : struct
    {
        int column = chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1;
        chunk.Changes![column].Stamp(row, version, added, ComponentType<T>.TracksAddedType);
    }

    internal void StampCreated(Chunk chunk, int start, int count, ulong version = 0)
    {
        if (chunk.Changes is null || count == 0) return;
        if (version == 0) version = NextVersion();
        foreach (ComponentType type in chunk.Archetype.Tracked)
        {
            ref ComponentChanges changes = ref chunk.Changes[chunk.Columns[type.Id].ColumnPlusOne - 1];
            for (int row = start; row < start + count; row++) changes.Stamp(row, version, true, type.TracksAdded);
        }
    }

    internal void StampRange<T>(Chunk chunk, int start, int count, ref ulong version) where T : struct
    {
        if (count == 0) return;
        ulong current = WriteVersion(ref version);
        ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1];
        changes.StampRange(start, count, current, ComponentType<T>.TracksAddedType, publish: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPendingCleanup(Entity entity)
        => IsAlive(entity) && HasDestroyed(in Records[entity.Id]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasDestroyed(in EntityRecord record)
    {
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<Destroyed>.TypeId;
        return (uint)type < (uint)columns.Length && columns[type].Has(record.Row);
    }

    private void DestroyRetained(Entity entity, ref EntityRecord record)
    {
        if (HasDestroyed(in record)) return;
        ComponentType[] components = record.Chunk!.Archetype.Components;
        AddKnown(entity, ref record, new Destroyed());
        foreach (ComponentType type in components)
            if (!type.IsCleanup && type.Id != ComponentType<Destroyed>.TypeId) type.Remove(this, entity, ref record);
        _sparse?.RemoveForDestroy(this, entity);
        ReleaseAfterCleanup(entity, ref record);
    }

    private void ReleaseAfterCleanup(Entity entity)
    {
        ref EntityRecord record = ref Resolve(entity);
        ReleaseAfterCleanup(entity, ref record);
    }

    private void ReleaseAfterCleanup(Entity entity, ref EntityRecord record)
    {
        Chunk chunk = record.Chunk!;
        int destroyed = ComponentType<Destroyed>.TypeId;
        if ((uint)destroyed >= (uint)chunk.Columns.Length || !chunk.Columns[destroyed].Has(record.Row)) return;
        foreach (ComponentType type in chunk.Archetype.Cleanup)
            if (chunk.Columns[type.Id].Has(record.Row)) return;
        if (_sparse is { } sparse && sparse.First[entity.Id] != 0) return;
        ReleaseEntity(entity, ref record);
    }

    public void ClearRemoved<T>(ulong through = ulong.MaxValue) where T : struct
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        int type = ComponentType<Removed<T>>.TypeId;
        CachedComponent? cached = (uint)type < (uint)_cachedByType.Length ? _cachedByType[type] : null;
        int destroyedType = ComponentType<Destroyed>.TypeId;
        for (int i = 0; i < ArchetypeCount; i++)
        {
            foreach (Chunk chunk in Archetypes[i].AllChunks)
            {
                if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                ulong[]? presence = chunk.Columns[type].Presence;
                bool hasDestroyed = (uint)destroyedType < (uint)chunk.Columns.Length && chunk.Columns[destroyedType].ColumnPlusOne != 0;
                for (int word = (chunk.Count - 1) >> 6; word >= 0; word--)
                {
                    int count = Math.Min(64, chunk.Count - (word << 6));
                    ulong rows = presence is null ? ulong.MaxValue : presence[word];
                    if (count < 64) rows &= (1UL << count) - 1;
                    while (rows != 0)
                    {
                        int bit = BitOperations.Log2(rows);
                        rows &= ~(1UL << bit);
                        int row = (word << 6) + bit;
                        ref Removed<T> value = ref ColumnEntry.At<Removed<T>>(ref chunk.Columns[type], row);
                        if (value.Version > through) continue;
                        Entity entity = chunk.Entities[row];
                        using MembershipEdit membership = BeginMembershipEdit(entity);
                        if (_retainRemovedColumns)
                        {
                            if (ComponentType<Removed<T>>.HasValueOperationsType)
                                ComponentType<Removed<T>>.ReleaseValue(this, ref value);
                            value = default;
                            presence ??= EnsurePresence(chunk, type);
                            presence[word] &= ~(1UL << bit);
                            cached?.Remove(entity);
                            if (hasDestroyed) ReleaseAfterCleanup(entity, ref Records[entity.Id]);
                        }
                        else RemoveValueKnown<Removed<T>>(entity, ref Records[entity.Id]);
                    }
                }
            }
        }
    }
}
