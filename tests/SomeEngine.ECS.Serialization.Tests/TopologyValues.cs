using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Serialization.Tests;

internal static class TopologyValues
{
    internal static Entity[] Children<T>(World world, Entity parent) where T : struct, IHierarchyDomain
    {
        using ChildrenRead children = Hierarchy<T>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }

    internal static RelationEdge<T>[] Outgoing<T>(World world, Entity entity) where T : struct
    {
        using RelationRead read = Relations<T>.Outgoing(world, entity);
        return read.Entries.ToArray().Select(static entry => new RelationEdge<T>(entry.Edge)).ToArray();
    }

    internal static RelationEdge<T>[] Incoming<T>(World world, Entity entity) where T : struct
    {
        using RelationRead read = Relations<T>.Incoming(world, entity);
        return read.Entries.ToArray().Select(static entry => new RelationEdge<T>(entry.Edge)).ToArray();
    }

    internal static RelationEdge<T>[] Incident<T>(World world, Entity entity) where T : struct
    {
        using IncidentRelationsRead read = Relations<T>.Incident(world, entity);
        var edges = new List<RelationEdge<T>>();
        foreach (RelationEntry entry in read) edges.Add(new(entry.Edge));
        return edges.ToArray();
    }

    internal static RelationEdge<T>[] Between<T>(World world, Entity source, Entity target) where T : struct
    {
        using RelationRead read = Relations<T>.Outgoing(world, source);
        return read.Entries.ToArray().Where(entry => entry.Other == target)
            .Select(static entry => new RelationEdge<T>(entry.Edge)).ToArray();
    }
}
