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

public struct SingleSharedFilter : IChunkFilter
{
    private readonly QuerySharedFilter _filter;
    private readonly bool _canMatch;
    private QueryArchetypeMatch? _match;
    private int _sharedSlot;

    internal static SingleSharedFilter NoMatch => default;

    internal SingleSharedFilter(QuerySharedFilter filter)
    {
        _filter = filter;
        _canMatch = true;
        _match = null;
        _sharedSlot = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(QueryArchetypeMatch match, Chunk chunk)
    {
        if (!_canMatch)
            return false;

        if (!ReferenceEquals(_match, match))
        {
            _match = match;
            _sharedSlot = match.SharedSlot(_filter.ComponentId);
        }

        return _sharedSlot >= 0 &&
               chunk.SharedValues is { } values &&
               values[_sharedSlot] == _filter.SharedIndex;
    }
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

    internal QueryChunkEnumerator(
        World world,
        QueryState plan,
        uint last,
        uint current,
        TFilter filter,
        bool matchRowFilters)
    {
        _world = world;
        _plan = plan;
        _lastSystemVersion = last;
        _currentSystemVersion = current;
        _matchRowFilters = matchRowFilters;
        _filter = filter;
        _matchIndex = 0;
        _chunkIndex = -1;
        _currentMatch = null;
        _currentChunk = null;
        _current = default;
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
        var matches = _plan.Matches;
        while (_matchIndex < matches.Length)
        {
            var match = matches[_matchIndex];
            _chunkIndex++;
            ReadOnlySpan<Chunk> chunks = match.Archetype.Chunks;
            if (_chunkIndex < chunks.Length)
            {
                var chunk = chunks[_chunkIndex];
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
}

