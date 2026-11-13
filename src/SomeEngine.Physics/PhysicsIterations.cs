using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IPhysicsIterations
{
    JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency);
}

/// <summary>Refreshes current contact geometry between numerical iteration ranges of the same substep.</summary>
public readonly struct PhysicsIterations<TSolver, TRefresh>(TSolver solver, TRefresh refresh, int iterations, int contactInterval)
    : IPhysicsStage where TSolver : IPhysicsIterations where TRefresh : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        int interval = contactInterval > 0 ? contactInterval : iterations;
        for (int first = 0; first < iterations; first += interval)
        {
            PhysicsStepContext step = context with { FirstIteration = first };
            if (first != 0) dependency = refresh.Schedule(in step, dependency);
            dependency = solver.ScheduleIterations(in step, Math.Min(interval, iterations - first), dependency);
        }
        return dependency;
    }
}

public static class PhysicsIterations
{
    public static PhysicsIterations<TSolver, TRefresh> Create<TSolver, TRefresh>(TSolver solver, TRefresh refresh,
        int iterations, int contactInterval) where TSolver : IPhysicsIterations where TRefresh : struct, IPhysicsStage =>
        new(solver, refresh, iterations, contactInterval);
}
