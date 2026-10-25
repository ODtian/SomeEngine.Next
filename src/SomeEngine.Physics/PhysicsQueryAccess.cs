using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

internal static class PhysicsQueryAccess
{
    internal static void AddAccesses(World world, QueryHandle query, List<JobResourceAccess> accesses)
    {
        QueryDefinition definition = world.GetQueryDefinition(query);
        var queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, queryAccesses);
        accesses.AddRange(queryAccesses);
    }
}
