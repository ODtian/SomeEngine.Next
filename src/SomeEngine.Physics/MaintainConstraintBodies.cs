using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Removes selected constraints whose original protected participation edges have ended.</summary>
public readonly struct MaintainConstraintBodies : IPhysicsStage, IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _constraints;
    private readonly JobResourceAccess[] _accesses;
    public MaintainConstraintBodies(World world, QueryHandle constraints)
    {
        _world = world;
        _constraints = world.Query(ConstraintBodies.AddReads(world.GetQueryDefinition(constraints).AsReadOnly()));
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, _constraints, accesses);
        _accesses = accesses.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.ScheduleParallel(new Check(this), 1, _accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        return WorldJobs.GetScheduler(context.World).ScheduleFinally(new Release(commands), JobScheduleOptions.Default, playback);
    }
    private readonly struct Check(MaintainConstraintBodies maintenance) : IJobParallelCommandProducer
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            var read = new ReadMissing(maintenance._world, commands);
            maintenance._world.ExecuteReadQuery(maintenance._constraints, ref read,
                static (scoped QueryCursor rows, ref ReadMissing state) => state.Execute(rows));
        }
    }
    private ref struct ReadMissing(World world, JobCommandWriter commands)
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref readonly ConstraintBodyLink link in row.ReadBuffer<ConstraintBodyLink>().AsSpan())
                {
                    if (world.Has<DirectedRelationEndpoints<ConstraintParticipant>>(link.Participant.Entity)
                        && world.GetDirectedRelationEndpoints(link.Participant).Source == row.Entity) continue;
                    _commands.DestroyEntity(row.Entity);
                    break;
                }
        }
    }
    private readonly record struct Release(JobCommandBuffer Commands) : IJob
    {
        public void Execute() => Commands.Dispose();
    }
    public void Dispose() => _world.ReleaseQuery(_constraints);
}
