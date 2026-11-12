using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemGroup
{
    public SystemState Add<TAction>(in TAction action, Query? query = null, CommandBatch? commands = null,
        bool parallel = true) where TAction : struct, IChunkAction
    {
        var system = new ChunkSystem<TAction>(_runner, world, scheduler, query ?? world.Query(ChunkActionTypes<TAction>.Query),
            action, commands, parallel);
        system.State.Index = _systems.Count; _systems.Add(system); _changed = true; return system.State;
    }

    private sealed class ChunkSystem<TAction>(SystemRunner runner, World world, Scheduler scheduler,
        Query query, TAction action, CommandBatch? commands, bool parallel)
        : SystemEntry(TAction.AdditionalAccesses, commands is not null)
        where TAction : struct, IChunkAction
    {
        protected override void AddComponents()
        {
            foreach (ChunkAccess component in ChunkActionTypes<TAction>.Components)
                Values.Add(new(component.Type, WorldValueKind.Table, component.WritesValue, component.Tracked, component.Structural));
            for (int type = 0; type < ComponentTypes.Count; type++)
                if (query.Description.Changed.Has(type)) Values.Add(new(type, WorldValueKind.Changes, false, false));
        }
        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!; jobs.BeginUse(); JobHandle rows;
            try { rows = runner.Schedule(query, action, dependency, parallel, State); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return scheduler.ScheduleStages(new SystemCompletion(world, jobs, State, commands), rows);
        }
    }
}
