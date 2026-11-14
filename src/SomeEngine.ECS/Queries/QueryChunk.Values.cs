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
        return new QueryChunk(_chunk, _start + start, count, _writes, _jobVersion);
    }

    public bool Has<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)_chunk.Columns.Length && _chunk.Columns[type].ColumnPlusOne != 0;
    }

    public bool Has<T>(int row) where T : struct
        => ComponentType<T>.IsSparseType ? _chunk.Archetype.World.Has<T>(_chunk.Entities[_start + row])
            : Has<T>() && _chunk.Columns[ComponentType<T>.TypeId].Has(_start + row);

    /// <summary>Reads one row's value; a sparse value is read from its store.</summary>
    public ref readonly T Read<T>(int row) where T : struct
    {
        if (ComponentType<T>.IsSparseType) return ref _chunk.Archetype.World.Read<T>(_chunk.Entities[_start + row]);
        if (ComponentType<T>.IsSharedType) throw new InvalidOperationException("Use Shared<T>(row) to read a shared value.");
        if (!InJob) _chunk.Archetype.World.RequireValue<T>(false);
        return ref ColumnEntry.At<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _start + row);
    }

    public bool TryRead<T>(out ReadOnlySpan<T> values) where T : struct
    {
        if (!Has<T>()) { values = default; return false; }
        values = Read<T>();
        return true;
    }

    /// <summary>Writes one row's value; a sparse value is written in its store.</summary>
    public ref T Write<T>(int row) where T : struct
    {
        World world = _chunk.Archetype.World;
        if (ComponentType<T>.IsSparseType) return ref world.Get<T>(_chunk.Entities[_start + row]);
        if (ComponentType<T>.IsSharedType || ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        if (InJob)
        {
            if (ComponentType<T>.TracksWritesType)
                ChangesOf(ComponentType<T>.TypeId).StampWord(_start + row, _jobVersion, ComponentType<T>.TracksAddedType);
        }
        else
        {
            RequireWrite();
            world.RequireValue<T>(true);
            world.RequireWriteRow(_chunk, _start + row);
            if (ComponentType<T>.TracksWritesType) World.Stamp<T>(_chunk, _start + row, world.NextVersion());
        }
        return ref ColumnEntry.At<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _start + row);
    }

    public bool WasWritten<T>(int row, ulong since) where T : struct
        => Changed<T>(row, since, false);

    /// <summary>Reads the write summary of the stored chunk column.</summary>
    public bool WasWritten<T>(ulong since) where T : struct
    {
        if (!ComponentType<T>.TracksWritesType || !Has<T>()) return false;
        _chunk.Archetype.World.RequireValue<T>(false,
            ComponentType<T>.Value.UsesStructureWrites ? WorldValueKind.Table : WorldValueKind.Changes);
        return _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1].LastWrite > since;
    }

    public bool WasAdded<T>(int row, ulong since) where T : struct
        => Changed<T>(row, since, true);

    private bool Changed<T>(int row, ulong since, bool added) where T : struct
    {
        if (!ComponentType<T>.TracksWritesType || added && !ComponentType<T>.TracksAddedType || !Has<T>(row)) return false;
        _chunk.Archetype.World.RequireValue<T>(false,
            ComponentType<T>.Value.UsesStructureWrites ? WorldValueKind.Table : WorldValueKind.Changes);
        ref ComponentChanges changes = ref _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1];
        ulong version = ComponentType<T>.TracksAddedType
            ? added ? Unsafe.As<RowVersions[]>(changes.Rows)[_start + row].Added
                : Unsafe.As<RowVersions[]>(changes.Rows)[_start + row].Written
            : Unsafe.As<ulong[]>(changes.Rows)[_start + row];
        return version > since;
    }
}
