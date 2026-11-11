using System.Reflection;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.ECS.Relations;

public enum RelationDirection { Directed, Undirected }
public enum RelationCardinality { Parallel, UniquePair, UniqueSource, UniqueTarget, OneToOne }
public enum RelationAdjacencyRole { Outgoing, Incoming, Incident }
public enum RelationAdjacencyOrderPolicy { Unordered, Ordered }

[AttributeUsage(AttributeTargets.Struct)]
public sealed class RelationSchemaAttribute(RelationDirection direction, RelationCardinality cardinality) : Attribute
{
    public RelationDirection Direction { get; } = direction;
    public RelationCardinality Cardinality { get; } = cardinality;
    public bool AllowSelfEdge { get; init; } = true;
}

public readonly record struct RelationSchema(RelationDirection Direction, RelationCardinality Cardinality, bool AllowSelfEdge)
{
    public static void Register<T>(RelationSchema schema) where T : struct => RelationType<T>.Register(schema);

    public static RelationSchema For<T>() where T : struct
    {
        RelationSchema schema = RelationType<T>.Schema;
        if (schema.Direction == RelationDirection.Undirected
            && schema.Cardinality is RelationCardinality.UniqueSource or RelationCardinality.UniqueTarget)
            throw new InvalidOperationException("Undirected cardinality uses UniquePair or OneToOne.");
        return schema;
    }
}

internal static class RelationType<T> where T : struct
{
    private sealed class SchemaValue(RelationSchema value)
    {
        internal readonly RelationSchema Value = value;
    }

    private static SchemaValue? s_schema;

    internal static RelationSchema Schema
    {
        get
        {
            if (Volatile.Read(ref s_schema) is { } found) return found.Value;
            var declared = new SchemaValue(Read());
            return (Interlocked.CompareExchange(ref s_schema, declared, null) ?? declared).Value;
        }
    }

    internal static void Register(RelationSchema schema)
    {
        var declared = new SchemaValue(schema);
        SchemaValue? existing = Interlocked.CompareExchange(ref s_schema, declared, null);
        DevelopmentCheck.Require(existing is null || existing.Value == schema,
            "A relation type uses one schema for its lifetime.");
    }

    private static RelationSchema Read()
    {
        var declaration = typeof(T).GetCustomAttribute<RelationSchemaAttribute>();
        return declaration is null ? new(RelationDirection.Directed, RelationCardinality.Parallel, true)
            : new(declaration.Direction, declaration.Cardinality, declaration.AllowSelfEdge);
    }
}
