using System.Runtime.CompilerServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

internal static class ShaderCodeJobs
{
    private static readonly ConditionalWeakTable<Scheduler, JobCategory> Categories = new();

    internal static JobCategory GetCategory(Scheduler scheduler)
    {
        lock (Categories)
            return Categories.GetValue(scheduler, static executor => executor.CreateCategory(new()
            {
                Name = "shader code",
                MaxInFlight = 256,
                MaxExecuting = Math.Clamp(executor.WorkerCount, 1, 4),
                ServiceShare = 1,
            }));
    }
}
