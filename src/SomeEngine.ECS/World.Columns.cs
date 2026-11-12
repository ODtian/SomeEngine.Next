using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private Transition AdditionByTypes(Chunk source, int row, TypeSet required)
    {
        Dictionary<TypeSet, Transition> additions = source.Archetype.AdditionsByTypes ??= new();
        if (additions.TryGetValue(required, out var found)) return found;
        int[] types = source.Archetype.Types.Types.Concat(required.Types).Distinct().ToArray();
        Archetype target = FindWithGroups(new TypeSet(false, types), source, row);
        var transition = new Transition(target.WritableChunk, source.Archetype.Pod, source.Archetype.Managed, null);
        additions.Add(required, transition);
        return transition;
    }

    internal Archetype ColumnsTarget(Entity entity, TypeSet required)
    {
        ref EntityRecord record = ref Resolve(entity);
        Chunk source = record.Chunk!;
        return source.Archetype.Types.Bits.Contains(required.Bits) ? source.Archetype : AdditionByTypes(source, record.Row, required).Target.Archetype;
    }

    internal void EnsureColumns(Entity entity, TypeSet required)
    {
        ref EntityRecord record = ref Resolve(entity);
        Chunk source = record.Chunk!;
        if (source.Archetype.Types.Bits.Contains(required.Bits)) return;
        MoveKnown(entity, ref record, source, AdditionByTypes(source, record.Row, required));
        Chunk target = record.Chunk!;
        foreach (int type in required.Types)
        {
            if (source.Archetype.Types.Bits.Has(type)) continue;
            EnsurePresence(target, type)[record.Row >> 6] &= ~(1UL << (record.Row & 63));
            ComponentType component = ComponentTypes.Get(type);
            if (component.TracksWrites) target.Changes![target.Columns[type].ColumnPlusOne - 1].ClearRow(record.Row, component.TracksAdded);
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Remove(entity);
        }
    }

    internal unsafe void EnsureColumns(Chunk source, TypeSet required)
    {
        int count = source.Count;
        Transition edge = AdditionByTypes(source, 0, required);
        Chunk target = edge.Target;
        int start = target.Count;
        target.Reserve(start + count);
        ColumnEntry[] sourceColumns = source.Columns, targetColumns = target.Columns;
        foreach (PodColumn description in edge.Pod)
            new ReadOnlySpan<byte>((void*)sourceColumns[description.TypeId].Rows, count * description.Size)
                .CopyTo(new Span<byte>((void*)(targetColumns[description.TypeId].Rows + (nint)start * description.Size), count * description.Size));
        foreach (ComponentType type in edge.Managed)
            Array.Copy(sourceColumns[type.Id].Values!, 0, targetColumns[type.Id].Values!, start, count);
        foreach (ComponentType type in source.Archetype.Shared)
            Array.Copy(sourceColumns[type.Id].Values!, 0, targetColumns[type.Id].Values!, start, count);
        source.Entities.AsSpan(0, count).CopyTo(target.Entities.AsSpan(start));
        target.Count += count;
        for (int row = 0; row < count; row++)
        {
            ref EntityRecord record = ref Records[source.Entities[row].Id];
            record.Chunk = target; record.Row = start + row;
            target.InitializePresence(start + row);
        }
        foreach (ComponentType type in source.Archetype.Tracked)
        {
            ref ComponentChanges before = ref source.Changes![sourceColumns[type.Id].ColumnPlusOne - 1];
            ref ComponentChanges after = ref target.Changes![targetColumns[type.Id].ColumnPlusOne - 1];
            for (int row = 0; row < count; row++) after.Copy(start + row, ref before, row, type.TracksAdded);
            Array.Clear(before.Rows, 0, count); Array.Clear(before.Words); Array.Clear(before.Blocks); before.LastWrite = 0;
        }
        foreach (ComponentType type in source.Archetype.Components)
        {
            ulong[]? present = sourceColumns[type.Id].Presence;
            if (present is not null)
            {
                ulong[] destination = EnsurePresence(target, type.Id);
                for (int row = 0; row < count; row++)
                    if ((present[row >> 6] & (1UL << (row & 63))) == 0)
                    {
                        int moved = start + row;
                        destination[moved >> 6] &= ~(1UL << (moved & 63));
                    }
            }
            if (source.Enabled?[sourceColumns[type.Id].ColumnPlusOne - 1] is { } enabled)
                for (int row = 0; row < count; row++)
                    if (sourceColumns[type.Id].Has(row) && (enabled[row >> 6] & (1UL << (row & 63))) == 0)
                        SetEnabled(target, type.Id, start + row, false);
            if (present is not null) Array.Clear(present);
        }
        foreach (int type in required.Types)
        {
            if (source.Archetype.Types.Bits.Has(type)) continue;
            ulong[] presence = EnsurePresence(target, type);
            ComponentType component = ComponentTypes.Get(type);
            for (int row = start; row < start + count; row++)
            {
                presence[row >> 6] &= ~(1UL << (row & 63));
                if (component.TracksWrites) target.Changes![targetColumns[type].ColumnPlusOne - 1].ClearRow(row, component.TracksAdded);
            }
        }
        foreach (ComponentType type in source.Archetype.Managed) Array.Clear(sourceColumns[type.Id].Values!, 0, count);
        if (source.Enabled is { } sourceEnabled)
            foreach (ulong[]? words in sourceEnabled) if (words is not null) Array.Clear(words);
        if (source.DisabledCounts is { } disabled) Array.Clear(disabled);
        source.Count = 0;
        if (target.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.BindRange(target, start, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteColumn<T>(Entity entity, ref EntityRecord record, in T value, ulong version) where T : struct
    {
        Chunk chunk = record.Chunk!;
        int row = record.Row, type = ComponentType<T>.TypeId;
        ref ColumnEntry column = ref chunk.Columns[type];
        bool added = !column.Has(row);
        if (ComponentType<T>.HasValueOperationsType && !added) ComponentType<T>.ReleaseValue(this, ref ColumnEntry.At<T>(ref column, row));
        if (!ComponentType<T>.IsTagType) ColumnEntry.At<T>(ref column, row) = value;
        if (added)
        {
            chunk.RestorePresence(type, row);
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Bind(entity, chunk, row);
        }
        if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, row, version, added);
    }
}
