using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

internal static class PhysicsQueryAccess
{
    internal static void AddAccesses(World world, Query query, List<JobResourceAccess> accesses) =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectReadAccess(query));

    internal static void AddAccesses<TComponent, TResources>(World world, Query query, List<JobResourceAccess> accesses)
        where TComponent : struct where TResources : struct, IJobComponentResources<TComponent> =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectReadAccess<TComponent, TResources>(query));
}
