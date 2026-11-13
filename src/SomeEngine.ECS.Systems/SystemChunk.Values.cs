using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Systems;

public readonly ref partial struct SystemChunk
{
    public bool HasSparse<T>(int row) where T : struct
        => _chunk.Archetype.World.Has<T>(_chunk.Entities[_start + row]);
    public ref readonly T ReadSparse<T>(int row) where T : struct
        => ref _chunk.Archetype.World.Read<T>(_chunk.Entities[_start + row]);
    public ref T GetSparse<T>(int row) where T : struct
        => ref _chunk.Archetype.World.Get<T>(_chunk.Entities[_start + row]);
    public bool Has<T>() where T : struct => new QueryChunk(_chunk, _start, Count).Has<T>();
    public bool Has<T>(int row) where T : struct => new QueryChunk(_chunk, _start, Count).Has<T>(row);
    public bool TryRead<T>(out ReadOnlySpan<T> values) where T : struct
        => new QueryChunk(_chunk, _start, Count).TryRead(out values);
    public OptionalComponent<T> Optional<T>(int row) where T : struct
        => new QueryChunk(_chunk, _start, Count).Optional<T>(row);
    public bool WasWritten<T>(int row, ulong since) where T : struct
        => new QueryChunk(_chunk, _start, Count).WasWritten<T>(row, since);
    public bool WasWritten<T>(ulong since) where T : struct
        => new QueryChunk(_chunk, _start, Count).WasWritten<T>(since);
    public bool WasAdded<T>(int row, ulong since) where T : struct
        => new QueryChunk(_chunk, _start, Count).WasAdded<T>(row, since);

    public ref T Write<T>(int row) where T : struct
    {
        if (ComponentType<T>.IsSharedType || ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write entry.");
        if (ComponentType<T>.TracksWritesType)
            _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1]
                .StampWord(_start + row, _version, ComponentType<T>.TracksAddedType);
        return ref ColumnEntry.At<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _start + row);
    }
}
