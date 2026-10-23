using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Maps a query's existing storage declarations to the World Job resources.</summary>
public static class QueryJobAccess
{
    public static int Count(QueryDefinition definition) => definition.JobStorageAccesses.Length + 1;

    /// <summary>Copies topology followed by the query's normalized storage accesses.</summary>
    public static int CopyAccesses(World world, QueryDefinition definition, Span<JobResourceAccess> destination)
    {
        destination[0] = definition.HasRelationshipWrite
            ? RelationshipJobAccess.TopologyWrite(world)
            : RelationshipJobAccess.TopologyRead(world);
        int count = 1;
        foreach (WorldJobStorageAccess storage in definition.JobStorageAccesses.Span)
        {
            if (storage.Kind == WorldStorageKind.Table)
            {
                ref readonly ComponentInfo info = ref ComponentRegistry.Get(storage.ComponentId);
                if (!info.IsJobAliasFree)
                {
                    if (!JobStorageTypeShape.IsAliasFree(info.Type))
                        throw new InvalidOperationException($"Query Job access requires alias-free {info.Type.Name} storage.");
                    ComponentRegistry.MarkJobAliasFree(storage.ComponentId);
                }
            }
            var key = new WorldStorageResourceKey(storage.Kind, storage.ComponentId);
            destination[count++] = storage.Access == WorldStorageAccess.Write
                ? WorldStorageJobResources.Write(world, key)
                : WorldStorageJobResources.Read(world, key);
        }
        return count;
    }
}
