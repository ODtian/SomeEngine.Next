using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Submits a concrete calculation for the selected bodies and substep interval.</summary>
public interface IPhysicsStage
{
    JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency);
}

/// <summary>Connects two concrete stages through their existing Job completions.</summary>
public readonly struct PhysicsStages<TFirst, TSecond>(TFirst first, TSecond second) : IPhysicsStage
    where TFirst : struct, IPhysicsStage
    where TSecond : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        second.Schedule(in context, first.Schedule(in context, dependency));
}

public static class PhysicsStages
{
    public static PhysicsStages<TFirst, TSecond> Then<TFirst, TSecond>(this TFirst first, TSecond second)
        where TFirst : struct, IPhysicsStage
        where TSecond : struct, IPhysicsStage => new(first, second);
}
