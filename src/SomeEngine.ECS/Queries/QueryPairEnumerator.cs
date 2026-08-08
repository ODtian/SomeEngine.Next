using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public delegate void QueryPairExecution<TWrite, TRead>(
    QueryPairEnumerator<TWrite, TRead> chunks)
    where TWrite : struct, IComponent
    where TRead : struct, IComponent;

public delegate void QueryPairExecution<TWrite, TRead, TState>(
    QueryPairEnumerator<TWrite, TRead> chunks,
    ref TState state)
    where TWrite : struct, IComponent
    where TRead : struct, IComponent;

public readonly ref struct QueryChunkPair<TWrite, TRead>
    where TWrite : struct, IComponent
    where TRead : struct, IComponent
{
    private readonly Span<TWrite> _write;
    private readonly ReadOnlySpan<TRead> _read;
    private readonly int _count;

    internal QueryChunkPair(Span<TWrite> write, ReadOnlySpan<TRead> read, int count)
    {
        _write = write;
        _read = read;
        _count = count;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    public Span<TWrite> Write
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _write[.._count];
    }

    public ReadOnlySpan<TRead> Read
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _read[.._count];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TWrite DangerousWriteRef()
    {
        return ref MemoryMarshal.GetReference(_write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TRead DangerousReadRef()
    {
        return ref MemoryMarshal.GetReference(_read);
    }
}

public ref struct QueryPairEnumerator<TWrite, TRead>
    where TWrite : struct, IComponent
    where TRead : struct, IComponent
{
    private readonly World _world;
    private readonly ReadOnlySpan<ReadWriteMatch> _matches;
    private readonly uint _lastSystemVersion;
    private readonly int _writeComponentId;
    private int _matchIndex;
    private int _chunkIndex;
    private int _writeColumn;
    private int _readColumn;
    private ReadWriteMatch _currentMatch;
    private ReadWriteMatch _singleMatch;
    private bool _hasCurrentMatch;
    private bool _currentHasFilter;
    private bool _singleFastPath;
    private bool _singleMatchReturned;
    private QueryChunkPair<TWrite, TRead> _current;

    internal QueryPairEnumerator(
        World world,
        QueryHandle handle,
        uint lastSystemVersion)
    {
        _world = world;
        _writeComponentId = ComponentMetadata<TWrite>.Id;
        var readComponentId = ComponentMetadata<TRead>.Id;
        _matches = world.AccessMatches<TWrite, TRead>(
            handle,
            _writeComponentId,
            readComponentId);
        _lastSystemVersion = lastSystemVersion;
        _matchIndex = 0;
        _chunkIndex = -1;
        _writeColumn = -1;
        _readColumn = -1;
        _currentMatch = default;
        ReadOnlySpan<ReadWriteMatch> matches = _matches;
        if (matches.Length == 1 && !matches[0].HasChangedFilter)
        {
            _singleMatch = matches[0];
            _singleFastPath = true;
        }
        else
        {
            _singleMatch = default;
            _singleFastPath = false;
        }

        _singleMatchReturned = false;
        _hasCurrentMatch = false;
        _currentHasFilter = false;
        _current = default;
    }

    public QueryChunkPair<TWrite, TRead> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryPairEnumerator<TWrite, TRead> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        bool moved = _singleFastPath
            ? MoveSingle()
            : MoveNextSlow();
        return moved;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MoveSingle()
    {
        if (_singleMatchReturned)
            return false;

        ReadOnlySpan<Chunk> chunks = _singleMatch.Archetype.Chunks;
        if (chunks.Length != 1)
        {
            _singleFastPath = false;
            return MoveNextSlow();
        }

        _singleMatchReturned = true;
        var chunk = chunks[0];
        if (chunk.Count == 0)
            return false;

        _writeColumn = _singleMatch.WriteColumn;
        _readColumn = _singleMatch.ReadColumn;
        LoadCurrent(chunk);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MoveNextSlow()
    {
        ReadOnlySpan<ReadWriteMatch> matches = _matches;
        while (true)
        {
            if (!_hasCurrentMatch)
            {
                if (_matchIndex >= matches.Length)
                    return false;

                var match = matches[_matchIndex];
                LoadMatch(match);
            }

            ReadOnlySpan<Chunk> chunks = _currentMatch.Archetype.Chunks;
            _chunkIndex++;
            if (_chunkIndex < chunks.Length)
            {
                var chunk = chunks[_chunkIndex];
                if (chunk.Count > 0 &&
                    (!_currentHasFilter || _currentMatch.Match.MatchesChanged(chunk, _lastSystemVersion)))
                {
                    LoadCurrent(chunk);
                    return true;
                }

                continue;
            }

            _matchIndex++;
            _chunkIndex = -1;
            _hasCurrentMatch = false;
            _currentHasFilter = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LoadMatch(ReadWriteMatch match)
    {
        _writeColumn = match.WriteColumn;
        _readColumn = match.ReadColumn;
        _currentMatch = match;
        _hasCurrentMatch = true;
        _currentHasFilter = match.HasChangedFilter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LoadCurrent(Chunk chunk)
    {
        _world.Components.WriteChunk(chunk, _writeColumn, _writeComponentId);
        _current = new QueryChunkPair<TWrite, TRead>(
            chunk.ComponentRows<TWrite>(_writeColumn),
            chunk.ComponentRows<TRead>(_readColumn),
            chunk.Count);
    }
}

