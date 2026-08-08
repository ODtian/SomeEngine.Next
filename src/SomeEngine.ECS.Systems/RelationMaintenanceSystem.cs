using SomeEngine.ECS.Components;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Schedules deferred canonical-endpoint to derived-adjacency reconciliation for one relation payload type.
/// </summary>
public static class RelationMaintenanceSystem<T>
    where T : struct, IComponent
{
    public static JobHandle Schedule(World world, JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        var job = new MaintenanceJob(world);
        return WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            RelationJobAccess<T>.EndpointsRead(world),
            in job,
            dependency);
    }

    private readonly struct MaintenanceJob : IJob
    {
        private readonly World _world;

        internal MaintenanceJob(World world)
        {
            _world = world;
        }

        public void Execute()
        {
            RelationJobAccess<T>.Maintain(_world);
        }
    }
}
