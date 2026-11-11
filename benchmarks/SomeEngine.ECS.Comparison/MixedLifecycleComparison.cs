using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class MixedLifecycleComparison
{
    internal static void Check(int count)
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        foreach (var library in ArchiveComparison.Libraries(count, true, scheduler))
        {
            FullArchiveWorld world = library.Create();
            MixedLifecycleJobs jobs = world.ConnectJobs(scheduler);
            long began = world.ResourceReleases;
            using (IDisposable snapshot = world.Capture(true))
            {
                jobs.Schedule(); world.Clear(); jobs.CheckCompleted(); world.CheckCleared();
                if (world.ResourceReleases - began != 16 || !world.InitialResourceReleased)
                    throw new InvalidOperationException(library.Name + ": mixed clear resource ownership differs.");
                world.Restore(snapshot); world.Check(true);
                jobs.Schedule(); world.Dispose(); jobs.CheckCompleted(); world.CheckDisposed();
                if (world.ResourceReleases - began != 32)
                    throw new InvalidOperationException(library.Name + ": restored mixed disposal differs.");
            }
            if (world.ResourceReleases - began != 48)
                throw new InvalidOperationException(library.Name + ": retained snapshot final disposal differs.");
            using FullArchiveWorld grown = library.Create();
            MixedLifecycleJobs grownJobs = grown.ConnectJobs(scheduler);
            grown.Grow(); grown.Check(true); grownJobs.RecordGrowth(1000); grownJobs.Schedule();
            grown.Clear(); grownJobs.CheckCompleted(); grown.CheckCleared();
            Console.WriteLine(library.Name + ": mixed clear, restored data, observer completion and world/snapshot resource ownership passed.");
        }
    }
}
