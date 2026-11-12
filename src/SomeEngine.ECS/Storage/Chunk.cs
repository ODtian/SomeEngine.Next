using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Storage;

internal sealed unsafe class Chunk
{
    internal readonly Archetype Archetype;
    internal readonly int Id;
    internal readonly ColumnEntry[] Columns;
    internal Entity[] Entities;
    internal int Count;
    internal int Capacity;
    internal Chunk? NextAvailable;
    internal bool InAvailable;
    internal ulong[]?[]? Enabled;
    internal int[]? DisabledCounts;
    internal ComponentChanges[]? Changes;

    internal Chunk(Archetype archetype, int capacity)
    {
        Archetype = archetype;
        Id = archetype.World.NextChunkId();
        Capacity = capacity;
        Entities = new Entity[capacity];
        int[] types = archetype.Types.Types;
        Columns = new ColumnEntry[types.Length == 0 ? 0 : types[^1] + 1];
        for (int i = 0; i < archetype.Components.Length; i++)
        {
            ComponentType type = archetype.Components[i];
            Columns[type.Id] = !type.IsTag && !type.IsShared && !type.ContainsReferences
                ? archetype.World.PodBuffer(type).Add(this, capacity, i + 1)
                : type.Allocate(capacity, i + 1);
        }
        if (archetype.Groups is { } groups)
            for (int i = 0; i < groups.Groupings.Length; i++)
                Columns[groups.Groupings[i].Type].GroupPlusOne = i + 1;
        if (archetype.Tracked.Length != 0)
        {
            Changes = new ComponentChanges[archetype.Components.Length];
            foreach (ComponentType type in archetype.Tracked)
                Changes[Columns[type.Id].ColumnPlusOne - 1] = new ComponentChanges(capacity, type.TracksAdded);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunk EnsureRow()
    {
        if (Count < Capacity) return this;
        if (Archetype.World.ChunkCapacity != 0) return Archetype.NextWritableChunk();
        Grow(Count + 1);
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reserve(int needed)
    {
        if (needed > Capacity) Grow(needed);
    }

    internal void ReserveRows(int needed)
    {
        if (needed > Capacity) Grow(needed, true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed, bool reserveRows = false)
    {
        int capacity = reserveRows ? needed : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, Math.Max(16, Capacity * 2)));
        Array.Resize(ref Entities, capacity);
        foreach (ComponentType type in Archetype.Components)
        {
            if (!type.IsTag)
            {
                if (!type.IsShared && !type.ContainsReferences) Archetype.World.PodBuffer(type).Grow(this, capacity);
                else type.Grow(ref Columns[type.Id], Count, capacity);
            }
            if (Columns[type.Id].Presence is not null)
                Array.Resize(ref Columns[type.Id].Presence, (capacity + 63) >> 6);
        }
        if (Enabled is { } enabled)
            for (int i = 0; i < enabled.Length; i++)
                if (enabled[i] is not null) Array.Resize(ref enabled[i], (capacity + 63) >> 6);
        if (Changes is { } changes)
            foreach (ComponentType type in Archetype.Tracked)
                changes[Columns[type.Id].ColumnPlusOne - 1].Grow(capacity, type.TracksAdded);
        Capacity = capacity;
        if (Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.BindRange(this, 0, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RemoveRow(int row, EntityRecord[] records, bool markedRows = false)
    {
        int last = --Count;
        if (Archetype.World.ChunkCapacity != 0) Archetype.MakeAvailable(this);
        MoveChanges(last, row);
        if (Enabled is not null) MoveEnabled(last, row);
        if (row != last)
        {
            Entity moved = Entities[last];
            Entities[row] = moved;
            ref EntityRecord movedRecord = ref records[moved.Id];
            movedRecord.Row = markedRows ? (movedRecord.Row & int.MinValue) | row : row;
            ColumnEntry[] columns = Columns;
            foreach (PodColumn description in Archetype.Pod)
            {
                ref ColumnEntry column = ref columns[description.TypeId];
                CopyBytes(column.Rows + (nint)last * description.Size,
                    column.Rows + (nint)row * description.Size, description.Size);
            }
            foreach (ComponentType type in Archetype.Managed)
                type.Copy(ref columns[type.Id], last, ref columns[type.Id], row);
            foreach (ComponentType type in Archetype.Shared)
            {
                int[] ids = Unsafe.As<int[]>(columns[type.Id].Values!);
                ids[row] = ids[last];
            }
            if ((Archetype.TransferKinds & 4) != 0) MovePresence(last, row);
            if (Archetype.Cached is { } cached)
                foreach (CachedComponent data in cached) data.Bind(moved, this, row);
        }
        foreach (ComponentType type in Archetype.Managed) type.Clear(ref Columns[type.Id], last);
        if (row == last && (Archetype.TransferKinds & 4) != 0) MovePresence(last, last);
    }

    internal void MoveChanges(int last, int row)
    {
        if (Changes is not { } changes) return;
        foreach (ComponentType type in Archetype.Tracked)
        {
            ref ComponentChanges values = ref changes[Columns[type.Id].ColumnPlusOne - 1];
            if (row != last) values.Copy(row, ref values, last, type.TracksAdded);
            values.ClearRow(last, type.TracksAdded);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void InitializePresence(int row)
    {
        if ((Archetype.TransferKinds & 4) == 0) return;
        foreach (int type in Archetype.Types.Types) Columns[type].SetPresent(row);
        if (Enabled is { } enabled)
            foreach (ulong[]? words in enabled)
                if (words is not null) words[row >> 6] |= 1UL << (row & 63);
    }

    internal bool IsEnabled(int type, int row)
    {
        ulong[]? words = Enabled?[Columns[type].ColumnPlusOne - 1];
        return words is null || (words[row >> 6] & (1UL << (row & 63))) != 0;
    }

    internal void RestorePresence(int type, int row)
    {
        ref ColumnEntry column = ref Columns[type];
        if (column.Has(row)) return;
        column.SetPresent(row);
        if (Enabled?[column.ColumnPlusOne - 1] is { } words)
            words[row >> 6] |= 1UL << (row & 63);
    }

    internal void RemoveEnabled(int type, int row)
    {
        int column = Columns[type].ColumnPlusOne - 1;
        if (Enabled?[column] is { } words && (words[row >> 6] & (1UL << (row & 63))) == 0)
            DisabledCounts![column]--;
    }

    internal void MoveEnabled(int last, int row)
    {
        if (Enabled is not { } enabled) return;
        for (int i = 0; i < enabled.Length; i++)
        {
            if (enabled[i] is not { } words) continue;
            int type = Archetype.Components[i].Id;
            if (Columns[type].Has(row) && (words[row >> 6] & (1UL << (row & 63))) == 0)
                DisabledCounts![i]--;
            ulong present = (words[last >> 6] >> (last & 63)) & 1;
            words[row >> 6] = (words[row >> 6] & ~(1UL << (row & 63))) | (present << (row & 63));
            words[last >> 6] &= ~(1UL << (last & 63));
        }
    }

    private void MovePresence(int last, int row)
    {
        if ((Archetype.TransferKinds & 4) == 0) return;
        foreach (int type in Archetype.Types.Types)
        {
            if (Columns[type].Presence is not { } words) continue;
            ulong present = (words[last >> 6] >> (last & 63)) & 1;
            words[row >> 6] = (words[row >> 6] & ~(1UL << (row & 63))) | (present << (row & 63));
            words[last >> 6] &= ~(1UL << (last & 63));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyBytes(nint source, nint target, int size)
    {
        if (size == 8) { *(ulong*)target = *(ulong*)source; return; }
        if (size == 4) { *(uint*)target = *(uint*)source; return; }
        if (size == 1) { *(byte*)target = *(byte*)source; return; }
        if (size <= 3)
        {
            Unsafe.WriteUnaligned((void*)target, Unsafe.ReadUnaligned<ushort>((void*)source));
            Unsafe.WriteUnaligned((void*)(target + size - 2), Unsafe.ReadUnaligned<ushort>((void*)(source + size - 2)));
            return;
        }
        if (size <= 7)
        {
            Unsafe.WriteUnaligned((void*)target, Unsafe.ReadUnaligned<uint>((void*)source));
            Unsafe.WriteUnaligned((void*)(target + size - 4), Unsafe.ReadUnaligned<uint>((void*)(source + size - 4)));
            return;
        }
        if (size <= 16)
        {
            Unsafe.WriteUnaligned((void*)target, Unsafe.ReadUnaligned<ulong>((void*)source));
            Unsafe.WriteUnaligned((void*)(target + size - 8), Unsafe.ReadUnaligned<ulong>((void*)(source + size - 8)));
            return;
        }
        Unsafe.CopyBlockUnaligned((void*)target, (void*)source, (uint)size);
    }
}
