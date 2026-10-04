using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views;

/// <summary>A component value, presence result, query membership, or environment source observed by a view.</summary>
public struct Dependency : IBufferElement
{
    public DependencyKind Kind;
    public Entity Entity;
    public ushort Component;
    public QueryHandle Query;
    public uint Version;

    internal readonly bool HasSameSource(in Dependency other) => Kind == other.Kind &&
        (Kind == DependencyKind.Membership
            ? Query.Equals(other.Query)
            : Entity == other.Entity && Component == other.Component);
}

/// <summary>The data observed by a view dependency.</summary>
public enum DependencyKind : byte
{
    Component,
    Presence,
    Membership,
    Environment,
}
