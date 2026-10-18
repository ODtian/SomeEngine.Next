using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

/// <summary>A relation description whose component reads are bound inside ECS query callbacks.</summary>
public interface IQueryRelationEnumerable<TRow>
{
    World World { get; }

    int QueryCount { get; }

    QueryHandle GetQuery(int position);

    QueryDefinition AddReads(QueryDefinition definition, int position);

    bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct;
}

public readonly ref struct RelationQueryViews
{
    public RelationQueryViews(QueryCursor first) : this(first, first, default) { }

    public RelationQueryViews(QueryCursor access, QueryCursor first, QueryCursor second)
    {
        Access = access;
        First = first;
        Second = second;
    }

    public QueryCursor Access { get; }

    public QueryCursor First { get; }

    public QueryCursor Second { get; }

    internal RelationQueryViews WithQuery(int position, QueryCursor query) =>
        new(Access, position == 0 ? query : First, position == 1 ? query : Second);
}
