using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public readonly ref struct QueryRow
{
    private readonly World _world;
    private readonly QueryArchetypeMatch _match;
    private readonly Chunk _chunk;
    private readonly int _row;

    internal QueryRow(
        World world,
        QueryArchetypeMatch match,
        Chunk chunk,
        int row,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        _world = world;
        _match = match;
        _chunk = chunk;
        _row = row;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public Entity Entity => _chunk.Entities[_row];

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

    public bool Has<T>() where T : struct =>
        _match.Archetype.HasComponent(ComponentMetadata<T>.Id);

    public T Read<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return _chunk.ReadComponent<T>(column, _row);
    }

    public ref T Write<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: false, write: true);
        return ref _world.Components.WriteRef<T>(
            Entity,
            _chunk,
            _row,
            column,
            CurrentSystemVersion);
    }

    public ref T ReadWrite<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: true);
        return ref _world.Components.WriteRef<T>(
            Entity,
            _chunk,
            _row,
            column,
            CurrentSystemVersion);
    }

    public void SetComponentEnabled<T>(bool enabled)
        where T : struct, IEnableableComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (!_match.Archetype.HasComponent(componentId))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} was not present on the current query row.");
        }

        int maskIndex = _match.Archetype.EnableMask(componentId);
        _chunk.WriteEnabled(maskIndex, _row, enabled);
    }

    public bool TryRead<T>(out T value) where T : struct
    {
        if (!Has<T>())
        {
            value = default;
            return false;
        }

        value = Read<T>();
        return true;
    }

    public DynamicBuffer<T> Buffer<T>()
        where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: false,
            write: true,
            out int headerColumn,
            out int inlineColumn);

        return new DynamicBuffer<T>(
            _world.Buffers,
            _chunk,
            _row,
            headerColumn,
            inlineColumn,
            CurrentSystemVersion);
    }

    public BufferView<T> ReadBuffer<T>()
        where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out int inlineColumn);

        return new BufferView<T>(
            _chunk,
            _row,
            headerColumn,
            inlineColumn);
    }
}

