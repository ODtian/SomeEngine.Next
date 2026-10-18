using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

/// <summary>A relation description whose component reads are bound inside ECS query callbacks.</summary>
public interface IQueryRelationEnumerable<TRow>
{
    World World { get; }

    QueryHandle GetQuery(int position);

    QueryDefinition AddReads(QueryDefinition definition, int position);

    bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct;
}

public readonly ref struct RelationQueryViews
{
    public RelationQueryViews(QueryCursor first) => First = first;

    public QueryCursor First { get; }
}
