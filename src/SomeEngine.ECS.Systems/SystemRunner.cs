using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemRunner(World world, Scheduler scheduler, int rangeRows = 1024)
{
    private readonly World _world = world;
    private readonly Scheduler _scheduler = scheduler;
    private readonly int _rangeRows = ((rangeRows + 63) >> 6) << 6;

    private readonly struct PartitionedRanges<TWork>(WorldJobs jobs, TWork work) : IRangeWork<QueryRange>
        where TWork : struct, IRangeWork<QueryRange>
    {
        public void Execute(in QueryRange range)
        {
            using var scope = jobs.EnterRange(range);
            work.Execute(in range);
        }
    }
}
