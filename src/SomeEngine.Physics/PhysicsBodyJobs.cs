using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

internal interface IPhysicsBodyWork
{
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, List<JobResourceAccess> accesses);
    void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies);
}

/// <summary>Captures current column permissions after dependencies, then retains the borrow through numerical completion.</summary>
internal static class PhysicsBodyJobs
{
    internal static JobHandle Schedule<TFields, TWork>(Query members, BodyColumns columns,
        in PhysicsStepContext context, in TWork work, JobHandle dependency)
        where TFields : struct, IBodyFields where TWork : struct, IPhysicsBodyWork =>
        (context.World.Execution!.Scheduler!).Schedule(new Capture<TFields, TWork>(members, columns, context, work),
            ((WorldJobs)context.World.Execution!).StructureRead, dependency: dependency);

    private readonly struct Capture<TFields, TWork>(Query members, BodyColumns columns, PhysicsStepContext context, TWork work) : IJob
        where TFields : struct, IBodyFields where TWork : struct, IPhysicsBodyWork
    {
        public void Execute()
        {
            var inputs = new List<JobResourceAccess>(); work.AddInputAccesses(context.World, inputs);
            if (inputs.Count == 0) ScheduleBody<TFields, TWork>(members, columns, in context, in work);
            else
            {
                inputs.AddRange(((WorldJobs)context.World.Execution!).StructureRead);
                (context.World.Execution!.Scheduler!).Schedule(new CaptureInputs<TFields, TWork>(members, columns, context, work), inputs.ToArray());
            }
        }
    }
    private readonly struct CaptureInputs<TFields, TWork>(Query members, BodyColumns columns, PhysicsStepContext context, TWork work) : IJob
        where TFields : struct, IBodyFields where TWork : struct, IPhysicsBodyWork
    {
        public void Execute() => ScheduleBody<TFields, TWork>(members, columns, in context, in work);
    }
    private static void ScheduleBody<TFields, TWork>(Query members, BodyColumns columns, in PhysicsStepContext context, in TWork work)
        where TFields : struct, IBodyFields where TWork : struct, IPhysicsBodyWork
    {
        var accesses = new List<JobResourceAccess>(BodyColumns.Accesses<TFields>(context.World, members));
        work.AddAccesses(context.World, accesses);
        (context.World.Execution!.Scheduler!).Schedule(new ExecuteBody<TFields, TWork>(members, columns, context, work), accesses.ToArray());
    }
    private readonly struct ExecuteBody<TFields, TWork>(Query members, BodyColumns columns, PhysicsStepContext context, TWork work) : IJob
        where TFields : struct, IBodyFields where TWork : struct, IPhysicsBodyWork
    {
        public void Execute()
        {
            using var scope = members.OpenRead();
            using var writes = context.World.OpenWriteBatch();
            PhysicsBodies bodies = columns.Bind<TFields>(context.World, members);
            work.Execute(in context, bodies);
        }
    }
}
