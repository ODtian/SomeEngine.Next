using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public readonly ref struct QueryChunkPair<TWrite, TRead>
    where TWrite : struct, IComponent
    where TRead : struct, IComponent
{
    private readonly TWrite[] _write;
    private readonly TRead[] _read;
    private readonly int _count;

    internal QueryChunkPair(TWrite[] write, TRead[] read, int count)
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
        get => _write.AsSpan(0, _count);
    }

    public ReadOnlySpan<TRead> Read
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _read.AsSpan(0, _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TWrite DangerousWriteRef()
    {
        return ref MemoryMarshal.GetArrayDataReference(_write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TRead DangerousReadRef()
    {
        return ref MemoryMarshal.GetArrayDataReference(_read);
    }
}

public ref struct QueryPairEnumerator<TWrite, TRead>
    where TWrite : struct, IComponent
    where TRead : struct, IComponent
{
    private readonly World _world;
    private readonly ReadWriteMatches _plan;
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
    private bool _started;
    private bool _disposed;

    internal QueryPairEnumerator(
        World world,
        QueryHandle handle,
        uint lastSystemVersion)
    {
        _world = world;
        _writeComponentId = ComponentMetadata<TWrite>.Id;
        var readComponentId = ComponentMetadata<TRead>.Id;
        _plan = world.AccessMatches<TWrite, TRead>(
            handle,
            _writeComponentId,
            readComponentId);
        _lastSystemVersion = lastSystemVersion;
        _matchIndex = 0;
        _chunkIndex = -1;
        _writeColumn = -1;
        _readColumn = -1;
        _currentMatch = default;
        var matches = _plan.Matches;
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
        _started = false;
        _disposed = false;
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
        EnsureStarted();

        if (_singleFastPath)
            return MoveSingle();

        return MoveNextSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MoveSingle()
    {
        if (_singleMatchReturned)
            return false;

        var chunks = _singleMatch.Archetype.Chunks;
        if (chunks.Count != 1)
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
        var matches = _plan.Matches;
        while (true)
        {
            if (!_hasCurrentMatch)
            {
                if (_matchIndex >= matches.Length)
                    return false;

                var match = matches[_matchIndex];
                LoadMatch(match);
            }

            var chunks = _currentMatch.Archetype.Chunks;
            _chunkIndex++;
            if (_chunkIndex < chunks.Count)
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
            Unsafe.As<TWrite[]>(chunk.Columns[_writeColumn]),
            Unsafe.As<TRead[]>(chunk.Columns[_readColumn]),
            chunk.Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureStarted()
    {
        if (_started)
            return;

        _started = true;
        _world.BeginIteration();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_started)
            _world.EndIteration();
    }
}

