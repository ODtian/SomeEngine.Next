using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class PhysicsSimulation
{
    public static PhysicsFrame<TStage, TConsumer> CreateFrame<TStage, TConsumer>(
        this PhysicsSimulation<TStage> simulation, TConsumer consumer, double stepDuration, double maximumDelay,
        long firstStepNumber, double startTime)
        where TStage : struct, IPhysicsStage
        where TConsumer : struct, IPhysicsStage =>
        new(simulation, consumer, stepDuration, maximumDelay, firstStepNumber, startTime);

    public static PhysicsSimulation<TStage> Create<TStage>(World world, QueryDefinition bodies, TStage stage, int substeps)
        where TStage : struct, IPhysicsStage => new(world, bodies, stage, substeps);
}

/// <summary>Uses the project's entity filter, concrete stages and explicit time subdivision.</summary>
public readonly struct PhysicsSimulation<TStage>(World world, QueryDefinition bodies, TStage stage, int substeps)
    where TStage : struct, IPhysicsStage
{
    internal JobHandle ScheduleConsumption<TConsumer>(TConsumer consumer, in PhysicsStepResult result)
        where TConsumer : struct, IPhysicsStage
    {
        var context = new PhysicsStepContext(world, bodies, result.Number, 0, 1, result.StartTime,
            result.EndTime - result.StartTime);
        return consumer.Schedule(in context, result.Completion);
    }

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
