using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Queries;

public ref struct QueryChangedChunkEnumerator
{
    private readonly World _world;
    private readonly QueryState _query;
    private readonly IEnumerator<Entity> _entities;
    private readonly HashSet<Chunk> _visited;
    private QueryChunkEnumerator<NoSharedFilter> _chunks;
    private readonly uint _last;
    private readonly uint _current;
    private bool _membershipComplete;
    private QueryChunkView _value;

    internal QueryChangedChunkEnumerator(World world, QueryState query,
        IEnumerable<Entity> entities, uint last, uint current)
    {
        _world = world;
        _query = query;
        _entities = entities.GetEnumerator();
        _visited = [];
        _chunks = new(world, query, last, current, default, matchRowFilters: true);
        _last = last;
        _current = current;
        _membershipComplete = false;
        _value = default;
    }

    public QueryChunkView Current => _value;
    public QueryChangedChunkEnumerator GetEnumerator() => this;
    public bool MoveNext()
    {
        if (!_membershipComplete)
        {
            while (_entities.MoveNext())
            {
                if (!_world.TryGetEntityQueryChunk(_query, _entities.Current, out var match, out var chunk) ||
                    !_visited.Add(chunk)) continue;
                if (!match.MatchesChanged(chunk, _last)) continue;
                _value = new(_world, match, chunk, _last, _current);
                return true;
            }
            _membershipComplete = true;
        }
        while (_chunks.MoveNext())
        {
            if (!_visited.Add(_chunks.CurrentChunk)) continue;
            foreach (QueryColumnAccess column in _chunks.CurrentMatch.AccessColumns)
            {
                if (!column.Access.CanRead() || column.ColumnIndex < 0 ||
                    !VersionClock.IsNewer(_chunks.CurrentChunk.ChangeVersions[column.ColumnIndex], _last)) continue;
                _value = _chunks.Current;
                return true;
            }
        }
        return false;
    }
    public void Dispose() => _entities.Dispose();
}
