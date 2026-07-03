using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

public ref struct QueryRowEnumerator<TFilter>
    where TFilter : IChunkFilter, allows ref struct
{
    private QueryChunkEnumerator<TFilter> _chunks;
    private QueryRowCursor _rows;

    internal QueryRowEnumerator(
        World world,
        QueryHandle handle,
        uint last,
        uint current,
        TFilter filter)
    {
        _chunks = new QueryChunkEnumerator<TFilter>(
            world,
            handle,
            last,
            current,
            filter,
            matchRowFilters: false);
        _rows = new QueryRowCursor(world, last, current);
    }

    public QueryRow Current => _rows.Current;

    public QueryRowEnumerator<TFilter> GetEnumerator() => this;

    public bool MoveNext()
    {
        if (!MoveNext(out QueryRow row))
            return false;

        _rows.Current = row;
        return true;
    }

    public bool MoveNext(out QueryRow row)
    {
        while (true)
        {
            if (_rows.MoveNext(out row))
                return true;

            if (!_chunks.MoveNext())
            {
                row = default;
                return false;
            }

            _rows.Reset(_chunks.CurrentMatch, _chunks.CurrentChunk);
        }
    }

    public void Dispose() => _chunks.Dispose();
}

internal struct QueryRowCursor
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
        Current = default;
    }

    internal QueryRow Current { get; set; }

    internal void Reset(QueryArchetypeMatch match, Chunk chunk)
    {
        _match = match;
        _chunk = chunk;
        _row = -1;
    }

    internal bool MoveNext()
    {
        if (!MoveNext(out QueryRow row))
            return false;

        Current = row;
        return true;
    }

    internal bool MoveNext(out QueryRow row)
    {
        if (_chunk is null)
        {
            row = default;
            return false;
        }

        var match = _match!;
        _row++;
        while (_row < _chunk.Count)
        {
            if (match.MatchesRow(_chunk, _row, _lastVersion))
            {
                row = new QueryRow(
                    _world,
                    match,
                    _chunk,
                    _row,
                    _lastVersion,
                    _currentVersion);
                return true;
            }

            _row++;
        }

        row = default;
        return false;
    }
}

public struct ChunkRowEnumerator
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

