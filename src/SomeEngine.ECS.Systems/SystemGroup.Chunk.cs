using SomeEngine.ECS.Commands;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemGroup
{
    /// <summary>Adds a generated chunk job; its command batch plays back when the system completes.</summary>
    public SystemState Add<TAction>(in TAction action, Query? query = null, CommandBatch? commands = null,
        bool parallel = true) where TAction : struct, IChunkAction
        => Register(new ChunkSystem<TAction>(_runner, world, _scheduler, query, action, commands, parallel));

    private sealed class ChunkSystem<TAction>(SystemRunner runner, World world, Scheduler scheduler,
        Query? query, TAction action, CommandBatch? commands, bool parallel) : SystemEntry
        where TAction : struct, IChunkAction
    {
        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!; jobs.BeginUse(); JobHandle rows;
            try { rows = runner.Schedule(query, action, dependency, parallel, State); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return scheduler.ScheduleStages(new SystemCompletion(world, jobs, State, commands), rows);
        }
    }
}
