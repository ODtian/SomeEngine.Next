using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class PhysicsSimulation
{
    public static PhysicsSimulation<TStage> Create<TStage>(World world, QueryDefinition bodies, TStage stage, int substeps)
        where TStage : struct, IPhysicsStage => new(world, bodies, stage, substeps);
}

/// <summary>Uses the project's entity filter, concrete stages and explicit time subdivision.</summary>
public readonly struct PhysicsSimulation<TStage>(World world, QueryDefinition bodies, TStage stage, int substeps)
    where TStage : struct, IPhysicsStage
{
    public PhysicsStepResult Schedule(long number, double startTime, double duration, JobHandle dependency = default)
    {
        double substepDuration = duration / substeps;
        for (int substep = 0; substep < substeps; substep++)
        {
            var context = new PhysicsStepContext(world, bodies, number, substep, substeps,
                startTime + substep * substepDuration, substepDuration);
            dependency = stage.Schedule(in context, dependency);
        }
        return new PhysicsStepResult(number, startTime, startTime + duration, dependency);
    }
}
