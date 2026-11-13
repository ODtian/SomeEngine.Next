using SomeEngine.ECS.Storage;
using SomeEngine.Core.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public readonly partial struct QueryChunk
{
    public QueryChunk Slice(int start, int count)
    {
        DevelopmentCheck.Require(start >= 0 && count >= 0 && start <= Count - count,
            "A query slice stays inside its chunk range.");
        return new QueryChunk(_chunk, _start + start, count, _write);
    }

    public bool Has<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)_chunk.Columns.Length && _chunk.Columns[type].ColumnPlusOne != 0;
    }

    public bool Has<T>(int row) where T : struct
        => Has<T>() && _chunk.Columns[ComponentType<T>.TypeId].Has(_start + row);

    public bool TryRead<T>(out ReadOnlySpan<T> values) where T : struct
    {
        if (!Has<T>()) { values = default; return false; }
        values = Read<T>();
        return true;
    }

    public ref T Write<T>(int row) where T : struct
    {
        RequireWrite();
        World world = _chunk.Archetype.World;
        world.RequireValueRange<T>(true, _chunk, _start + row, 1);
        world.RequireWriteRow(_chunk, _start + row);
        if (ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        if (ComponentType<T>.TracksWritesType) World.Stamp<T>(_chunk, _start + row, world.NextVersion());
        return ref ColumnEntry.At<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _start + row);
    }

    public bool WasWritten<T>(int row, ulong since) where T : struct
        => Changed<T>(row, since, false);

    /// <summary>Reads the write summary of the stored chunk column.</summary>
    public bool WasWritten<T>(ulong since) where T : struct
    {
        if (!ComponentType<T>.TracksWritesType || !Has<T>()) return false;
        _chunk.Archetype.World.RequireValueRange<T>(false, _chunk, _start, Count,
            ComponentType<T>.Value.UsesStructureWrites ? WorldValueKind.Table : WorldValueKind.Changes);
        return _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1].LastWrite > since;
    }

    public bool WasAdded<T>(int row, ulong since) where T : struct
        => Changed<T>(row, since, true);

    private bool Changed<T>(int row, ulong since, bool added) where T : struct
    {
        if (!ComponentType<T>.TracksWritesType || added && !ComponentType<T>.TracksAddedType || !Has<T>(row)) return false;
        _chunk.Archetype.World.RequireValueRange<T>(false, _chunk, _start + row, 1,
            ComponentType<T>.Value.UsesStructureWrites ? WorldValueKind.Table : WorldValueKind.Changes);
        ref ComponentChanges changes = ref _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1];
        ulong version = ComponentType<T>.TracksAddedType
            ? added ? Unsafe.As<RowVersions[]>(changes.Rows)[_start + row].Added
                : Unsafe.As<RowVersions[]>(changes.Rows)[_start + row].Written
            : Unsafe.As<ulong[]>(changes.Rows)[_start + row];
        return version > since;
    }
}
