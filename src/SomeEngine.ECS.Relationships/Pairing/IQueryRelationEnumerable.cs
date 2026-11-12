
namespace SomeEngine.ECS.Relations;

/// <summary>A relation description whose component reads remain inside its open ECS query scopes.</summary>
public interface IQueryRelationEnumerable<TRow>
{
    /// <summary>All emitted participant identities belong to the single input query.</summary>
    static virtual bool ReadsFromSingleQuery => false;

    /// <summary>The world declared by this description.</summary>
    World World { get; }

    int QueryCount { get; }

    /// <summary>Reads the participant query declared at this position.</summary>
    Query GetQuery(int position);

    /// <summary>Forms the access definition declared by this description at this position.</summary>
    QueryDescription AddReads(QueryDescription definition, int position);

    bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<TRow>, allows ref struct;
}
