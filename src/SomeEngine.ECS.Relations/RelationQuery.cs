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
        using RelationQueryReads reads = ReadQueries(QueryDefinition.Empty);
        var execution = new ReadViews<TOutput>(_enumerable, new(ref output));
        reads.Execute(ref execution, lastSystemVersion);
        return execution.Completed;
    }

    /// <summary>Registers the relation's member queries and a complete component access query for repeated borrowed execution.</summary>
    public RelationQueryReads ReadQueries(QueryDefinition additionalReads)
    {
        World world = _enumerable.World;
        int queryCount = _enumerable.QueryCount;
        RelationQueryHandles queries = default;
        QueryDefinition access = QueryDefinition.Empty.WithAccessFrom(additionalReads);
        if (queryCount == 0)
            access = _enumerable.AddReads(access, 0);
        for (int position = 0; position < queryCount; position++)
        {
            QueryDefinition definition = world.GetQueryDefinition(_enumerable.GetQuery(position));
            if (queryCount == 1 && TEnumerable.ReadsFromSingleQuery)
                definition = _enumerable.AddReads(definition, position);
            queries[position] = world.Query(definition);
            access = access.WithAccessFrom(definition);
            access = _enumerable.AddReads(access, position);
        }
        queries[queryCount] = world.Query(access);
        return new RelationQueryReads(world, queries, queryCount);
    }

    private ref struct ReadViews<TOutput>(TEnumerable enumerable, RelationOutputReference<TOutput> output) : IRelationQueryExecution
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct
    {
        private readonly RelationOutputReference<TOutput> _output = output;
        internal bool Completed;
        public void Execute(scoped RelationQueryViews views) => Completed = enumerable.Enumerate(views, ref _output.Value);
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
