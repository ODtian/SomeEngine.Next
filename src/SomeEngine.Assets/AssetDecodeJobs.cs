using System.Runtime.CompilerServices;
using SomeEngine.Job;

namespace SomeEngine.Assets;

internal static class AssetDecodeJobs
{
    private static readonly ConditionalWeakTable<Scheduler, JobCategory> Categories = new();

    internal static JobCategory GetCategory(Scheduler scheduler)
    {
        lock (Categories)
            return Categories.GetValue(scheduler, static executor => executor.CreateCategory(new()
            {
                Name = "asset decode",
                MaxInFlight = 256,
                MaxExecuting = Math.Max(1, executor.WorkerCount / 2),
                ServiceShare = 1,
            }));
    }
}
