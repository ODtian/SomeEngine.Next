using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Relations;

public enum RelationDirection : byte
{
    Directed,
    Undirected,
}

public enum RelationCardinality : byte
{
    Parallel,
    UniquePair,
    UniqueSource,
    UniqueTarget,
    OneToOne,
}

public enum RelationMaintenanceTiming : byte
{
    Immediate,
    Deferred,
}

/// <summary>
/// Declares the topology policy of a relation payload type. The policy is
/// cached once per closed payload type and is never stored on individual edges.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RelationSchemaAttribute : Attribute
{
    public RelationSchemaAttribute(
        RelationDirection direction,
        RelationCardinality cardinality = RelationCardinality.Parallel)
    {
        Direction = direction;
        Cardinality = cardinality;
    }

    public RelationDirection Direction { get; }

    public RelationCardinality Cardinality { get; }

    public bool AllowSelfEdge { get; set; } = true;
}

public readonly record struct RelationSchema(
    RelationDirection Direction,
    RelationCardinality Cardinality,
    bool AllowSelfEdge)
{
    public static RelationSchema For<T>() where T : struct, IComponent =>
        RelationSchemaCache<T>.Value;

    internal void ValidateFor(Type payloadType)
    {
        if (Direction != RelationDirection.Directed &&
            Direction != RelationDirection.Undirected)
        {
            throw new InvalidOperationException(
                $"Relation payload {payloadType.Name} declares an unknown direction {Direction}.");
        }

        if (Cardinality < RelationCardinality.Parallel ||
            Cardinality > RelationCardinality.OneToOne)
        {
            throw new InvalidOperationException(
                $"Relation payload {payloadType.Name} declares an unknown cardinality {Cardinality}.");
        }

        if (Direction == RelationDirection.Undirected &&
            (Cardinality == RelationCardinality.UniqueSource ||
             Cardinality == RelationCardinality.UniqueTarget))
        {
            throw new InvalidOperationException(
                $"Undirected relation payload {payloadType.Name} cannot declare {Cardinality}. " +
                "Use UniquePair or OneToOne.");
        }
    }
}

internal static class RelationSchemaCache<T> where T : struct, IComponent
{
    private static readonly Lazy<RelationSchema> s_value = new(Create);

    internal static RelationSchema Value => s_value.Value;

    private static RelationSchema Create()
    {
        var attribute = typeof(T)
            .GetCustomAttributes(typeof(RelationSchemaAttribute), inherit: false)
            .Cast<RelationSchemaAttribute>()
            .SingleOrDefault();
        if (attribute is null)
        {
            throw new InvalidOperationException(
                $"Relation payload {typeof(T).FullName} must declare RelationSchemaAttribute.");
        }

        var schema = new RelationSchema(
            attribute.Direction,
            attribute.Cardinality,
            attribute.AllowSelfEdge);
        schema.ValidateFor(typeof(T));
        return schema;
    }
}
