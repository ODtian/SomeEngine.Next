using System.ComponentModel;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Runs generated IJobEntity and IJobChunk work on the scheduler of the World's WorldJobs.
/// Code calls the generated job methods; generated code calls this runner.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed partial class SystemRunner
{
    private readonly World _world;
    private readonly Scheduler _scheduler;
    private readonly int _rangeRows, _chunkRangeRows;

    public SystemRunner(World world, int rangeRows = 1024)
    {
        _world = world;
        _scheduler = world.Execution is WorldJobs jobs ? jobs.Scheduler
            : throw new InvalidOperationException("Connect WorldJobs before scheduling generated work.");
        _rangeRows = ((rangeRows + 63) >> 6) << 6;
        _chunkRangeRows = rangeRows;
    }

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
