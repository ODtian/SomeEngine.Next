using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

/// <summary>Protected canonical directed endpoint pair.</summary>
public struct DirectedRelationEndpoints<T> : IRelationshipSource
    where T : struct, IComponent
{
    public Entity Source;
    public Entity Target;
}

/// <summary>
/// Protected canonical undirected endpoint pair. A/B slots remain fixed and
/// are not canonicalized even when uniqueness uses an unordered pair key.
/// </summary>
public struct UndirectedRelationEndpoints<T> : IRelationshipSource
    where T : struct, IComponent
{
    public Entity EndpointA;
    public Entity EndpointB;
}

internal struct AppliedRelationEndpoints<T> : IComponent
    where T : struct, IComponent
{
    public Entity EndpointA;
    public Entity EndpointB;
    public bool IsApplied;
}

/// <summary>Read-only derived outgoing-adjacency presence marker.</summary>
public readonly struct Outgoing<T> : IRelationshipTarget
    where T : struct, IComponent
{
    internal Outgoing(int count, uint generation)
    {
        Count = count;
        Generation = generation;
    }

    public int Count { get; }

    public uint Generation { get; }
}

/// <summary>Read-only derived incoming-adjacency presence marker.</summary>
public readonly struct Incoming<T> : IRelationshipTarget
    where T : struct, IComponent
{
    internal Incoming(int count, uint generation)
    {
        Count = count;
        Generation = generation;
    }

    public int Count { get; }

    public uint Generation { get; }
}

/// <summary>Read-only derived undirected incident-adjacency presence marker.</summary>
public readonly struct Incident<T> : IRelationshipTarget
    where T : struct, IComponent
{
    internal Incident(int count, uint generation)
    {
        Count = count;
        Generation = generation;
    }

    public int Count { get; }

    public uint Generation { get; }
}
