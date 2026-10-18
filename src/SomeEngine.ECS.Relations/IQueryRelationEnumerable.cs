using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

/// <summary>A relation description whose component reads are bound inside ECS query callbacks.</summary>
public interface IQueryRelationEnumerable<TRow>
{
    /// <summary>All emitted participant identities belong to the single input query.</summary>
    static virtual bool ReadsFromSingleQuery => false;

    World World { get; }

    int QueryCount { get; }

    QueryHandle GetQuery(int position);

    QueryDefinition AddReads(QueryDefinition definition, int position);

    bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct;
}
