namespace SomeEngine.ECS.Queries;

public readonly struct QueryHandle : IEquatable<QueryHandle>
{
    internal QueryHandle(int index, int version)
    {
        Index = index;
        Version = version;
    }

    internal int Index { get; }

    internal int Version { get; }

    public bool IsValid => Index >= 0 && Version > 0;

    public bool Equals(QueryHandle other) => Index == other.Index && Version == other.Version;

    public override bool Equals(object? obj) => obj is QueryHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Version);

    public override string ToString() => IsValid ? $"QueryHandle({Index}:{Version})" : "QueryHandle.Invalid";
}

