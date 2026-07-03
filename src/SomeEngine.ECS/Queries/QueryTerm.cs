namespace SomeEngine.ECS.Queries;

public enum QueryTermKind : byte
{
    All = 0,
    None = 1,
    Any = 2,
    Optional = 3,
}

[Flags]
public enum QueryTermFilter : byte
{
    None = 0,
    Added = 1,
    Changed = 2,
    ChunkChanged = 4,
    Enabled = 8,
    Disabled = 16,
}

public readonly struct QueryTerm : IEquatable<QueryTerm>
{
    public QueryTerm(
        int componentId,
        QueryTermKind kind,
        QueryAccess access = QueryAccess.None,
        QueryTermFilter filters = QueryTermFilter.None)
    {
        ComponentId = componentId;
        Kind = kind;
        Access = access;
        Filters = filters;
    }

    public int ComponentId { get; }

    public QueryTermKind Kind { get; }

    public QueryAccess Access { get; }

    public QueryTermFilter Filters { get; }

    public bool Equals(QueryTerm other) =>
        ComponentId == other.ComponentId &&
        Kind == other.Kind &&
        Access == other.Access &&
        Filters == other.Filters;

    public override bool Equals(object? obj) => obj is QueryTerm other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ComponentId, Kind, Access, Filters);

    public override string ToString() => $"{Kind}:{ComponentId}:{Access}:{Filters}";
}

public readonly struct QuerySharedFilter
{
    public QuerySharedFilter(int componentId, int sharedIndex)
    {
        ComponentId = componentId;
        SharedIndex = sharedIndex;
    }

    public int ComponentId { get; }

    public int SharedIndex { get; }
}

public readonly struct QueryAccessEntry
{
    public QueryAccessEntry(int componentId, QueryAccess access, QueryTermKind kind)
    {
        ComponentId = componentId;
        Access = access;
        Kind = kind;
    }

    public int ComponentId { get; }

    public QueryAccess Access { get; }

    public QueryTermKind Kind { get; }
}

