using SomeEngine.Job;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>Submits a concrete calculation for the selected bodies and substep interval.</summary>
public interface IPhysicsStage
{
    JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency);
}

/// <summary>Connects two concrete stages through their existing Job completions.</summary>
public readonly struct PhysicsStages<TFirst, TSecond>(TFirst first, TSecond second) : IPhysicsStage, IPhysicsStageComposition
    where TFirst : struct, IPhysicsStage
    where TSecond : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new PhysicsStageCommands(in context, dependency);
        Append(commands);
        return commands.Complete();
    }
    public void Append(PhysicsStageCommands commands) { commands.Append(first); commands.Append(second); }
}

/// <summary>Schedules selected work sets from the same preceding work and joins their existing Job completions.</summary>
public readonly struct PhysicsParallelStages<TFirst, TSecond>(TFirst first, TSecond second) : IPhysicsStage
    where TFirst : struct, IPhysicsStage
    where TSecond : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle firstWork = first.Schedule(in context, dependency);
        JobHandle secondWork = second.Schedule(in context, dependency);
        return (context.World.Execution!.Scheduler!).CombineDependencies([firstWork, secondWork]);
    }
}

public static class PhysicsStages
{
    public static PhysicsParallelStages<TFirst, TSecond> Parallel<TFirst, TSecond>(this TFirst first, TSecond second)
        where TFirst : struct, IPhysicsStage
        where TSecond : struct, IPhysicsStage => new(first, second);

    public static PhysicsStages<TFirst, TSecond> Then<TFirst, TSecond>(this TFirst first, TSecond second)
        where TFirst : struct, IPhysicsStage
        where TSecond : struct, IPhysicsStage => new(first, second);
}
