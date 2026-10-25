using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Maps a query's existing storage declarations to the World Job resources.</summary>
public static class QueryJobAccess
{
    public static int Count(QueryDefinition definition) => definition.JobStorageAccesses.Length + 1;

    /// <summary>Copies topology followed by the query's normalized storage accesses.</summary>
    public static int CopyAccesses(World world, QueryDefinition definition, Span<JobResourceAccess> destination) =>
        CopyAccessesCore(world, definition, destination, null);

    /// <summary>Includes managed read-only columns with registered component-resource descriptions.</summary>
    public static int CopyAccesses(World world, QueryDefinition definition, Span<JobResourceAccess> destination,
        QueryReadResources resources) => CopyAccessesCore(world, definition, destination, resources);

    private static int CopyAccessesCore(World world, QueryDefinition definition, Span<JobResourceAccess> destination,
        QueryReadResources? resources)
    {
        destination[0] = definition.HasRelationshipWrite
            ? RelationshipJobAccess.TopologyWrite(world)
            : RelationshipJobAccess.TopologyRead(world);
        int count = 1;
        foreach (WorldJobStorageAccess storage in definition.JobStorageAccesses.Span)
        {
            RequireStorage(storage, resources);
            var key = new WorldStorageResourceKey(storage.Kind, storage.ComponentId);
            destination[count++] = storage.Access == WorldStorageAccess.Write
                ? WorldStorageJobResources.Write(world, key)
                : WorldStorageJobResources.Read(world, key);
        }
        return count;
    }

    internal static void RequireStorage(WorldJobStorageAccess storage, QueryReadResources? resources)
    {
        if (storage.Kind != WorldStorageKind.Table) return;
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(storage.ComponentId);
        if (!info.IsJobAliasFree &&
            (storage.Access != WorldStorageAccess.Read || resources?.ContainsComponent(storage.ComponentId) != true))
            throw new InvalidOperationException($"Query Job access requires alias-free {info.Type.Name} storage.");
    }
}
