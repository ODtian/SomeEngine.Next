using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public readonly struct RelationQuery<TEnumerable, TRow> : IRelationEnumerable<TRow>
    where TEnumerable : struct, IQueryRelationEnumerable<TRow>
{
    public RelationQuery(TEnumerable enumerable) => Enumerable = enumerable;

    public TEnumerable Enumerable { get; }

    public bool Enumerate<TOutput>(ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct
        => EnumerateQuery(ref output, null);

    public bool Enumerate<TOutput>(uint lastSystemVersion, ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct
        => EnumerateQuery(ref output, lastSystemVersion);

    private bool EnumerateQuery<TOutput>(ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct
    {
        var identities = new QueryIdentityOutput<TOutput, TRow>(output);
        try
        {
            return EnumerateBoundQuery(ref identities, lastSystemVersion);
        }
        finally
        {
            output = identities.Output;
        }
    }

    private bool EnumerateBoundQuery<TOutput>(ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        World world = Enumerable.World;
        int queryCount = Enumerable.QueryCount;
        if (queryCount == 1 && TEnumerable.ReadsFromSingleQuery)
        {
            QueryDefinition definition = Enumerable.AddReads(world.GetQueryDefinition(Enumerable.GetQuery(0)), 0);
            if (!definition.CanWrite)
                return EnumerateSingle(definition, ref output, lastSystemVersion);
        }
        RelationQueryHandles queries = default;
        int acquired = 0;
        var execution = new RelationExecution<TOutput>(Enumerable, queries, queryCount,
            -1, default, lastSystemVersion, output);
        try
        {
            QueryDefinition access = QueryDefinition.Empty;
            if (queryCount == 0)
                access = Enumerable.AddReads(access, 0);
            for (int position = 0; position < queryCount; position++)
            {
                QueryDefinition definition = world.GetQueryDefinition(Enumerable.GetQuery(position));
                if (queryCount == 1 && TEnumerable.ReadsFromSingleQuery)
                    definition = Enumerable.AddReads(definition, position);
                queries[acquired++] = world.Query(definition);
                access = access.WithAccessFrom(definition);
                access = Enumerable.AddReads(access, position);
            }
            QueryHandle query = world.Query(access);
            queries[acquired++] = query;
            execution = new RelationExecution<TOutput>(Enumerable, queries, queryCount,
                -1, default, lastSystemVersion, output);
            if (lastSystemVersion is { } baseline)
                world.ExecuteQuery(query, baseline, ref execution);
            else
                world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            for (int index = acquired - 1; index >= 0; index--)
                world.ReleaseQuery(queries[index]);
        }
    }

    private bool EnumerateSingle<TOutput>(QueryDefinition definition, ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        World world = Enumerable.World;
        QueryHandle query = world.Query(definition);
        var execution = new SingleExecution<TOutput>(Enumerable, output);
        try
        {
            if (lastSystemVersion is { } baseline)
                world.ExecuteQuery(query, baseline, ref execution);
            else
                world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(query);
        }
    }

    private ref struct SingleExecution<TOutput>(TEnumerable enumerable, TOutput output) : IQueryExecution
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor query) => Completed = enumerable.Enumerate(new RelationQueryViews(query), ref Output);
    }

    public int Count() => CountRows(null);

    public int Count(uint lastSystemVersion) => CountRows(lastSystemVersion);

    private int CountRows(uint? lastSystemVersion)
    {
        var output = new RelationCount<TRow>();
        EnumerateQuery(ref output, lastSystemVersion);
        return output.Count;
    }

    public bool Any() => HasRows(null);

    public bool Any(uint lastSystemVersion) => HasRows(lastSystemVersion);

    private bool HasRows(uint? lastSystemVersion)
    {
        var output = new RelationAny<TRow>();
        EnumerateQuery(ref output, lastSystemVersion);
        return output.Found;
    }

    private ref struct RelationExecution<TOutput> : IQueryExecution
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        private TEnumerable _enumerable;
        private readonly RelationQueryHandles _queries;
        private readonly int _queryCount;
        private readonly int _position;
        private readonly RelationQueryViews _views;
        private readonly uint? _lastSystemVersion;

        public RelationExecution(TEnumerable enumerable, RelationQueryHandles queries,
            int queryCount, int position, RelationQueryViews views, uint? lastSystemVersion, TOutput output)
        {
            _enumerable = enumerable;
            _queries = queries;
            _queryCount = queryCount;
            _position = position;
            _views = views;
            _lastSystemVersion = lastSystemVersion;
            Output = output;
            Completed = false;
        }

        public bool Completed { get; private set; }

        public TOutput Output;

        public void Execute(QueryCursor query)
        {
            RelationQueryViews views = _position < 0
                ? new RelationQueryViews(query, default, default)
                : _views.WithQuery(_position, query);
            int nextPosition = _position + 1;
            if (nextPosition == _queryCount)
            {
                Completed = _enumerable.Enumerate(views, ref Output);
                return;
            }

            var execution = new RelationExecution<TOutput>(_enumerable, _queries, _queryCount,
                nextPosition, views, _lastSystemVersion, Output);
            try
            {
                if (_lastSystemVersion is { } baseline)
                    _enumerable.World.ExecuteQuery(_queries[nextPosition], baseline, ref execution);
                else
                    _enumerable.World.ExecuteQuery(_queries[nextPosition], ref execution);
                Completed = execution.Completed;
            }
            finally
            {
                Output = execution.Output;
            }
        }
    }
}

// Eight participant queries and their encompassing access query.
[InlineArray(9)]
internal struct RelationQueryHandles
{
    private QueryHandle _first;
}

internal struct RelationCount<TRow> : IRelationOutput<TRow>
{
    internal int Count;

    public bool Add(in TRow row)
    {
        Count++;
        return true;
    }
}

internal struct RelationAny<TRow> : IRelationOutput<TRow>
{
    internal bool Found;

    public bool Add(in TRow row)
    {
        Found = true;
        return false;
    }
}
