global using static SomeEngine.ECS.Tests.WorldReadback;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

internal static class WorldReadback
{
    internal static Entity[] ReadIndexedEntities<T, TKey>(World world, TKey key)
        where T : struct, IIndexedComponent<TKey> where TKey : notnull
    {
        using IndexMembers values = world.Index<T, TKey>().Find(key);
        return values.Entities.ToArray();
    }

    internal static Entity[] ReadChildren<TDomain>(World world, Entity parent) where TDomain : struct, IHierarchyDomain
    {
        using ChildrenRead children = Hierarchy<TDomain>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }

    internal static (Entity OtherEndpoint, RelationEdge<T> Edge)[] ReadOutgoing<T>(World world, Entity entity) where T : struct
    {
        using RelationRead read = Relations<T>.Outgoing(world, entity);
        var values = new (Entity, RelationEdge<T>)[read.Count];
        for (int index = 0; index < values.Length; index++) values[index] = (read.Entries[index].Other, new(read.Entries[index].Edge));
        return values;
    }

    internal static (Entity OtherEndpoint, RelationEdge<T> Edge)[] ReadIncoming<T>(World world, Entity entity) where T : struct
    {
        using RelationRead read = Relations<T>.Incoming(world, entity);
        var values = new (Entity, RelationEdge<T>)[read.Count];
        for (int index = 0; index < values.Length; index++) values[index] = (read.Entries[index].Other, new(read.Entries[index].Edge));
        return values;
    }

    internal static (Entity OtherEndpoint, RelationEdge<T> Edge)[] ReadIncident<T>(World world, Entity entity) where T : struct
    {
        using IncidentRelationsRead read = Relations<T>.Incident(world, entity);
        var values = new (Entity, RelationEdge<T>)[read.Count];
        int index = 0;
        foreach (RelationEntry entry in read) values[index++] = (entry.Other, new(entry.Edge));
        return values;
    }

    internal static RelationEdge<T>[] ReadPairEdges<T>(World world, Entity source, Entity target) where T : struct
    {
        var result = new List<RelationEdge<T>>();
        if (RelationSchema.For<T>().Direction == RelationDirection.Directed)
        {
            using RelationRead read = Relations<T>.Outgoing(world, source);
            foreach (RelationEntry entry in read) if (entry.Other == target) result.Add(new(entry.Edge));
        }
        else
        {
            using IncidentRelationsRead read = Relations<T>.Incident(world, source);
            foreach (RelationEntry entry in read) if (entry.Other == target) result.Add(new(entry.Edge));
        }
        return result.ToArray();
    }

    internal static RelationEndpoints<T> ReadEndpoints<T>(World world, RelationEdge<T> edge) where T : struct
    {
        using ReadLookup<RelationEndpoints<T>> values = world.OpenRead<RelationEndpoints<T>>();
        return values.Read(edge.Entity);
    }
}
