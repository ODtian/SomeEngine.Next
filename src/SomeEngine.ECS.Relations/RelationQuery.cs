using SomeEngine.ECS.Queries;

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
        World world = Enumerable.World;
        QueryDefinition definition = world.GetQueryDefinition(Enumerable.GetQuery(0));
        QueryHandle query = world.Query(Enumerable.AddReads(definition, 0));
        var execution = new RelationExecution<TOutput>(Enumerable, output);
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
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct
    {
        private TEnumerable _enumerable;

        public RelationExecution(TEnumerable enumerable, TOutput output)
        {
            _enumerable = enumerable;
            Output = output;
            Completed = false;
        }

        public bool Completed { get; private set; }

        public TOutput Output;

        public void Execute(QueryCursor query) =>
            Completed = _enumerable.Enumerate(new RelationQueryViews(query), ref Output);
    }
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
