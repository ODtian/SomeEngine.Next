using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

public ref struct QueryRowEnumerator<TFilter>
    where TFilter : IChunkFilter, allows ref struct
{
    private QueryChunkEnumerator<TFilter> _chunks;
    private QueryRowCursor _rows;

    internal QueryRowEnumerator(
        World world,
        QueryState plan,
        uint last,
        uint current,
        TFilter filter)
    {
        _chunks = new QueryChunkEnumerator<TFilter>(
            world,
            plan,
            last,
            current,
            filter,
            matchRowFilters: false);
        _rows = new QueryRowCursor(world, last, current);
    }

    public QueryRow Current => _rows.Current;

    internal QueryArchetypeMatch CurrentMatch => _rows.Match;
    internal Chunk? CurrentChunk => _rows.Chunk;

    public QueryRowEnumerator<TFilter> GetEnumerator() => this;

    public bool MoveNext()
    {
        while (true)
        {
            if (_rows.MoveNext())
                return true;

            if (!_chunks.MoveNext())
                return false;

            _rows.Reset(_chunks.CurrentMatch, _chunks.CurrentChunk);
        }
    }

    public bool MoveNext(out QueryRow row)
    {
        if (!MoveNext())
        {
            row = default;
            return false;
        }

        row = Current;
        return true;
    }

}

internal ref struct QueryRowCursor
{
    private readonly World _world;
    private readonly uint _lastVersion;
    private readonly uint _currentVersion;
    private QueryArchetypeMatch? _match;
    private Chunk? _chunk;
    private int _row;

    internal QueryRowCursor(World world, uint last, uint current)
    {
        _world = world;
        _lastVersion = last;
        _currentVersion = current;
        _match = null;
        _chunk = null;
        _row = -1;
    }

    internal readonly QueryRow Current => new(
        _world,
        _match!,
        _chunk!,
        _row,
        _lastVersion,
        _currentVersion);

    internal QueryArchetypeMatch Match => _match!;
    internal Chunk? Chunk => _chunk;

    internal void Reset(QueryArchetypeMatch match, Chunk chunk)
    {
        _match = match;
        _chunk = chunk;
        _row = -1;
    }

    internal bool MoveNext()
    {
        if (_chunk is null)
            return false;

        var match = _match!;
        _row++;
        while (_row < _chunk.Count)
        {
            if (match.MatchesRow(_chunk, _row, _lastVersion))
                return true;

            _row++;
        }

        return false;
    }

    internal bool MoveNext(out QueryRow row)
    {
        if (!MoveNext())
        {
            row = default;
            return false;
        }

        row = Current;
        return true;
    }
}

public ref struct ChunkRowEnumerator
{
    private QueryRowCursor _rows;

    internal ChunkRowEnumerator(
        World world,
        QueryArchetypeMatch match,
        Chunk chunk,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        _rows = new QueryRowCursor(world, lastSystemVersion, currentSystemVersion);
        _rows.Reset(match, chunk);
    }

    public QueryRow Current => _rows.Current;

    public ChunkRowEnumerator GetEnumerator() => this;

    public bool MoveNext() => _rows.MoveNext();

    public bool MoveNext(out QueryRow row) => _rows.MoveNext(out row);
}

