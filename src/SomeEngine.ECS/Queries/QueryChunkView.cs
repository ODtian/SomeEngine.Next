using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public readonly ref struct QueryChunkView
{
    private readonly World _world;
    private readonly QueryArchetypeMatch _match;
    private readonly Chunk _chunk;

    internal QueryChunkView(
        World world,
        QueryArchetypeMatch match,
        Chunk chunk,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        _world = world;
        _match = match;
        _chunk = chunk;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            RequireChunkAccess();
            return _chunk.Count;
        }
    }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

    public ReadOnlySpan<Entity> Entities
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            RequireChunkAccess();
            return _chunk.Entities[.._chunk.Count];
        }
    }

    public ChunkRowEnumerator Rows =>
        new(_world, _match, _chunk, LastSystemVersion, CurrentSystemVersion);

    public ChunkRowIndexEnumerator RowIndices =>
        new(_match, _chunk, LastSystemVersion);

    public bool Has<T>() where T : struct =>
        _match.Archetype.HasComponent(ComponentMetadata<T>.Id);

    public bool HasBuffer<T>() where T : struct, IBufferElement =>
        _match.Archetype.HasComponent(BufferComponents.Header<T>()) &&
        _match.Archetype.HasComponent(BufferComponents.Inline<T>());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetChangeVersion<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return _chunk.ChangeVersions[column];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<uint> ReadWriteVersions<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return _chunk.WriteVersionRows(column)[.._chunk.Count];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChangedSinceLastSystemVersion<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return SomeEngine.ECS.VersionClock.IsNewer(
            _chunk.ChangeVersions[column],
            LastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RowChangedSinceLastSystemVersion<T>(int row) where T : struct
    {
        RequireRow(row);
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return SomeEngine.ECS.VersionClock.IsNewer(
            _chunk.WriteVersionRows(column)[row],
            LastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasBufferChangedSinceLastSystemVersion<T>()
        where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out _);
        return SomeEngine.ECS.VersionClock.IsNewer(
            _chunk.ChangeVersions[headerColumn],
            LastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RowBufferChangedSinceLastSystemVersion<T>(int row)
        where T : struct, IBufferElement
    {
        RequireRow(row);
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out _);
        return SomeEngine.ECS.VersionClock.IsNewer(
            _chunk.WriteVersionRows(headerColumn)[row],
            LastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Read<T>() where T : struct
    {
        RequireChunkAccess();
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return _chunk.ComponentRows<T>(column)[.._chunk.Count];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read<T>(int row) where T : struct
    {
        RequireRow(row);
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return _chunk.ReadComponent<T>(column, row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Write<T>() where T : struct
    {
        RequireChunkAccess();
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: false, write: true);
        return _world.Components.WriteChunk<T>(_chunk, column, CurrentSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Write<T>(int row) where T : struct
    {
        RequireRow(row);
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: false, write: true);
        return ref _world.Components.WriteRef<T>(
            _chunk.Entities[row],
            _chunk,
            row,
            column,
            CurrentSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> ReadWrite<T>() where T : struct
    {
        RequireChunkAccess();
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: true);
        return _world.Components.WriteChunk<T>(_chunk, column, CurrentSystemVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T ReadWrite<T>(int row) where T : struct
    {
        RequireRow(row);
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: true);
        return ref _world.Components.WriteRef<T>(
            _chunk.Entities[row],
            _chunk,
            row,
            column,
            CurrentSystemVersion);
    }

    public bool TryRead<T>(out ReadOnlySpan<T> span) where T : struct
    {
        RequireChunkAccess();
        if (!Has<T>())
        {
            span = default;
            return false;
        }

        span = Read<T>();
        return true;
    }

    public bool TryRead<T>(int row, out T value) where T : struct
    {
        RequireRow(row);
        if (!Has<T>())
        {
            value = default;
            return false;
        }

        value = Read<T>(row);
        return true;
    }

    public Entity GetEntity(int row)
    {
        RequireRow(row);
        return _chunk.Entities[row];
    }

    public void SetComponentEnabled<T>(int row, bool enabled)
        where T : struct, IEnableableComponent
    {
        RequireRow(row);
        int componentId = ComponentMetadata<T>.Id;
        if (!_match.Archetype.HasComponent(componentId))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} was not present on the current query row.");
        }

        int maskIndex = _match.Archetype.EnableMask(componentId);
        _chunk.WriteEnabled(maskIndex, row, enabled);
    }

    public DynamicBuffer<T> Buffer<T>(int row)
        where T : struct, IBufferElement
    {
        if ((uint)row >= (uint)_chunk.Count)
            throw new ArgumentOutOfRangeException(nameof(row));

        RequireChunkAccess();
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: false,
            write: true,
            out int headerColumn,
            out int inlineColumn);

        return new DynamicBuffer<T>(
            _world.Buffers,
            _chunk,
            row,
            headerColumn,
            inlineColumn,
            CurrentSystemVersion);
    }

    public BufferView<T> ReadBuffer<T>(int row)
        where T : struct, IBufferElement
    {
        if ((uint)row >= (uint)_chunk.Count)
            throw new ArgumentOutOfRangeException(nameof(row));

        RequireChunkAccess();
        QueryAccessGuards.RequireBufferAccess<T>(
            _match,
            read: true,
            write: false,
            out int headerColumn,
            out int inlineColumn);

        return new BufferView<T>(
            _chunk,
            row,
            headerColumn,
            inlineColumn);
    }

    private void RequireChunkAccess()
    {
        if (!_match.HasRowFilter)
            return;

        throw new InvalidOperationException(
            "Chunk span access cannot satisfy row filters. Use chunk.Rows, chunk.RowIndices, or QueryCursor.Rows.");
    }

    private void RequireRow(int row)
    {
        if ((uint)row >= (uint)_chunk.Count)
            throw new ArgumentOutOfRangeException(nameof(row));
    }
}

public struct ChunkRowIndexEnumerator
{
    private readonly QueryArchetypeMatch _match;
    private readonly Chunk _chunk;
    private readonly uint _lastSystemVersion;
    private int _row;

    internal ChunkRowIndexEnumerator(
        QueryArchetypeMatch match,
        Chunk chunk,
        uint lastSystemVersion)
    {
        _match = match;
        _chunk = chunk;
        _lastSystemVersion = lastSystemVersion;
        _row = -1;
        Current = -1;
    }

    public int Current { get; private set; }

    public ChunkRowIndexEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (!MoveNext(out int row))
            return false;

        Current = row;
        return true;
    }

    public bool MoveNext(out int row)
    {
        _row++;
        while (_row < _chunk.Count)
        {
            if (_match.MatchesRow(_chunk, _row, _lastSystemVersion))
            {
                row = _row;
                return true;
            }

            _row++;
        }

        row = -1;
        return false;
    }
}

