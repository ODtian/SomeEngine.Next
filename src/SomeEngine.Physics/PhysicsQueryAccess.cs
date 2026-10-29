using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

internal static class PhysicsQueryAccess
{
    internal static void AddAccesses(World world, QueryHandle query, List<JobResourceAccess> accesses,
        QueryReadResources? resources = null)
    {
        QueryDefinition definition = world.GetQueryDefinition(query);
        var queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        if (resources is null) QueryJobAccess.CopyAccesses(world, definition, queryAccesses);
        else QueryJobAccess.CopyAccesses(world, definition, queryAccesses, resources);
        accesses.AddRange(queryAccesses);
    }
}
