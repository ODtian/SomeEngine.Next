using System.Diagnostics.CodeAnalysis;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    public static bool TryGetScheduler(World world, [NotNullWhen(true)] out Scheduler? scheduler)
    {
        scheduler = world.Execution?.Scheduler;
        return scheduler is not null;
    }
}
