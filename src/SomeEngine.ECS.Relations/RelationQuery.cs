using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public struct RelationQuery<TEnumerable, TRow> : IRelationEnumerable<TRow>
    where TEnumerable : struct, IQueryRelationEnumerable<TRow>
{
    private TEnumerable _enumerable;

    public RelationQuery(TEnumerable enumerable) => _enumerable = enumerable;

    public readonly TEnumerable Enumerable => _enumerable;

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
            output = identities.Value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnumerateBoundQuery<TOutput>(ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        World world = _enumerable.World;
        int queryCount = _enumerable.QueryCount;
        if (queryCount == 1 && TEnumerable.ReadsFromSingleQuery)
        {
            QueryHandle members = _enumerable.GetQuery(0);
            QueryDefinition original = world.GetQueryDefinition(members);
            QueryDefinition definition = _enumerable.AddReads(original, 0);
            if (!definition.CanWrite)
                return EnumerateSingle(world, members, definition, !ReferenceEquals(original, definition),
                    ref output, lastSystemVersion);
        }
        return EnumerateQueries(world, queryCount, ref output, lastSystemVersion);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool EnumerateQueries<TOutput>(World world, int queryCount, ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        RelationQueryHandles queries = default;
        int acquired = 0;
        var execution = new RelationExecution<TOutput>(Enumerable, queries, queryCount,
            -1, default, lastSystemVersion, new(ref output));
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
                -1, default, lastSystemVersion, new(ref output));
            if (lastSystemVersion is { } baseline)
                world.ExecuteQuery(query, baseline, ref execution);
            else
                world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            for (int index = acquired - 1; index >= 0; index--)
                world.ReleaseQuery(queries[index]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnumerateSingle<TOutput>(World world, QueryHandle members, QueryDefinition definition,
        bool temporaryQuery, ref TOutput output, uint? lastSystemVersion)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        QueryHandle query = temporaryQuery ? world.Query(definition) : members;
        var execution = new SingleExecution<TOutput>
        {
            Enumerable = _enumerable,
            Output = new(ref output),
            Completed = false
        };
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
            if (temporaryQuery)
                world.ReleaseQuery(query);
        }
    }

    private ref struct SingleExecution<TOutput> : IQueryExecution
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        public TEnumerable Enumerable;
        public RelationOutputReference<TOutput> Output;
        public bool Completed;

        public void Execute(scoped QueryCursor query) => Completed = Enumerable.Enumerate(new RelationQueryViews(query), ref Output.Value);
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
            int queryCount, int position, RelationQueryViews views, uint? lastSystemVersion, RelationOutputReference<TOutput> output)
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

        public RelationOutputReference<TOutput> Output;

        public void Execute(scoped QueryCursor query)
        {
            RelationQueryViews views = _position < 0
                ? new RelationQueryViews(query, default, default)
                : _views.WithQuery(_position, query);
            int nextPosition = _position + 1;
            if (nextPosition == _queryCount)
            {
                Completed = _enumerable.Enumerate(views, ref Output.Value);
                return;
            }

            var execution = new RelationExecution<TOutput>(_enumerable, _queries, _queryCount,
                nextPosition, views, _lastSystemVersion, Output);
            if (_lastSystemVersion is { } baseline)
                _enumerable.World.ExecuteQuery(_queries[nextPosition], baseline, ref execution);
            else
                _enumerable.World.ExecuteQuery(_queries[nextPosition], ref execution);
            Completed = execution.Completed;
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
