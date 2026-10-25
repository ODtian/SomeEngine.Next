using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public interface IRelationQueryExecution
{
    void Execute(scoped RelationQueryViews views);
}

/// <summary>Holds registered query references and borrows their views together for synchronous callbacks.</summary>
public sealed class RelationQueryReads : IDisposable
{
    private readonly World _world;
    private readonly RelationQueryHandles _queries;
    private readonly int _queryCount;

    internal RelationQueryReads(World world, RelationQueryHandles queries, int queryCount)
    {
        _world = world;
        _queries = queries;
        _queryCount = queryCount;
    }

    public QueryHandle Access => _queries[_queryCount];

    public void Execute<TExecution>(ref TExecution execution)
        where TExecution : struct, IRelationQueryExecution, allows ref struct => Execute(ref execution, null);

    public void Execute<TExecution>(uint lastSystemVersion, ref TExecution execution)
        where TExecution : struct, IRelationQueryExecution, allows ref struct => Execute(ref execution, lastSystemVersion);

    internal void Execute<TExecution>(ref TExecution execution, uint? lastSystemVersion)
        where TExecution : struct, IRelationQueryExecution, allows ref struct
    {
        var read = new ReadViews<TExecution>(this, -1, default, lastSystemVersion, new(ref execution));
        if (lastSystemVersion is { } baseline)
            _world.ExecuteQuery(Access, baseline, ref read);
        else
            _world.ExecuteQuery(Access, ref read);
    }

    private readonly ref struct ReadViews<TExecution>(RelationQueryReads reads, int position,
        RelationQueryViews views, uint? lastSystemVersion, RelationOutputReference<TExecution> execution) : IQueryExecution
        where TExecution : struct, IRelationQueryExecution, allows ref struct
    {
        private readonly RelationQueryViews _views = views;
        private readonly RelationOutputReference<TExecution> _execution = execution;

        public void Execute(scoped QueryCursor query)
        {
            RelationQueryViews current = position < 0
                ? new RelationQueryViews(query, default, default)
                : _views.WithQuery(position, query);
            int next = position + 1;
            if (next == reads._queryCount)
            {
                _execution.Value.Execute(current);
                return;
            }
            var read = new ReadViews<TExecution>(reads, next, current, lastSystemVersion, _execution);
            if (lastSystemVersion is { } baseline)
                reads._world.ExecuteQuery(reads._queries[next], baseline, ref read);
            else
                reads._world.ExecuteQuery(reads._queries[next], ref read);
        }
    }

    public void Dispose()
    {
        for (int index = _queryCount; index >= 0; index--)
            _world.ReleaseQuery(_queries[index]);
    }
}
