using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

public interface IChunkFilter
{
    bool Matches(QueryArchetypeMatch match, Chunk chunk);
}

public readonly struct NoSharedFilter : IChunkFilter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(QueryArchetypeMatch match, Chunk chunk) => true;
}

public readonly struct SingleSharedFilter : IChunkFilter
{
    private readonly QuerySharedFilter _filter;
    private readonly bool _canMatch;

    internal static SingleSharedFilter NoMatch => default;

    internal SingleSharedFilter(QuerySharedFilter filter)
    {
        _filter = filter;
        _canMatch = true;
    }

    public bool Matches(QueryArchetypeMatch match, Chunk chunk) =>
        _canMatch && match.MatchesShared(chunk, _filter);
}

public readonly ref struct SpanSharedFilter : IChunkFilter
{
    private readonly ReadOnlySpan<QuerySharedFilter> _filters;

    internal SpanSharedFilter(ReadOnlySpan<QuerySharedFilter> filters)
    {
        _filters = filters;
    }

    public bool Matches(QueryArchetypeMatch match, Chunk chunk) =>
        match.MatchesShared(chunk, _filters);
}

public ref struct QueryChunkEnumerator<TFilter>
    where TFilter : IChunkFilter, allows ref struct
{
    private readonly World _world;
    private readonly QueryState _plan;
    private readonly uint _lastSystemVersion;
    private readonly uint _currentSystemVersion;
    private readonly bool _matchRowFilters;
    private TFilter _filter;
    private int _matchIndex;
    private int _chunkIndex;
    private QueryArchetypeMatch? _currentMatch;
    private Chunk? _currentChunk;
    private QueryChunkView _current;
    private bool _started;
    private bool _disposed;

    internal QueryChunkEnumerator(
        World world,
        QueryHandle handle,
        uint last,
        uint current,
        TFilter filter,
        bool matchRowFilters)
    {
        _world = world;
        _plan = world.GetQueryState(handle);
        _lastSystemVersion = last;
        _currentSystemVersion = current;
        _matchRowFilters = matchRowFilters;
        _filter = filter;
        _matchIndex = 0;
        _chunkIndex = -1;
        _currentMatch = null;
        _currentChunk = null;
        _current = default;
        _started = false;
        _disposed = false;
    }

    public QueryChunkView Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current;
    }

    internal QueryArchetypeMatch CurrentMatch => _currentMatch!;

    internal Chunk CurrentChunk => _currentChunk!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryChunkEnumerator<TFilter> GetEnumerator() => this;

    public bool MoveNext()
    {
        EnsureStarted();

        var matches = _plan.Matches;
        while (_matchIndex < matches.Count)
        {
            var match = matches[_matchIndex];
            _chunkIndex++;
            if (_chunkIndex < match.Archetype.Chunks.Count)
            {
                var chunk = match.Archetype.Chunks[_chunkIndex];
                if (chunk.Count > 0 &&
                    MatchesChanged(match, chunk) &&
                    _filter.Matches(match, chunk))
                {
                    LoadCurrent(match, chunk);
                    return true;
                }

                continue;
            }

            _matchIndex++;
            _chunkIndex = -1;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchesChanged(QueryArchetypeMatch match, Chunk chunk) =>
        _matchRowFilters
            ? match.MatchesChanged(chunk, _lastSystemVersion)
            : match.MatchesChunkFilter(chunk, _lastSystemVersion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LoadCurrent(QueryArchetypeMatch match, Chunk chunk)
    {
        _currentMatch = match;
        _currentChunk = chunk;
        _current = new QueryChunkView(
            _world,
            match,
            chunk,
            _lastSystemVersion,
            _currentSystemVersion);
    }

    private void EnsureStarted()
    {
        if (_started)
            return;

        _started = true;
        _world.BeginIteration();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_started)
            _world.EndIteration();
    }
}

