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

internal readonly struct QuerySharedFilter
{
    internal QuerySharedFilter(World world, int componentId, int sharedIndex)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        ComponentId = componentId;
        SharedIndex = sharedIndex;
    }

    private World World { get; }

    internal int ComponentId { get; }

    internal int SharedIndex { get; }

    internal void RequireWorld(World world)
    {
        if (!ReferenceEquals(World, world))
        {
            throw new InvalidOperationException(
                "A precomputed shared-component filter can only execute in its owning World.");
        }
    }
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

