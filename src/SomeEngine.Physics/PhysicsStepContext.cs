using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>A selected entity set and the time interval assigned to one numerical substep.</summary>
public readonly record struct PhysicsStepContext(
    World World,
    QueryDefinition Bodies,
    long Number,
    int Substep,
    int Substeps,
    double StartTime,
    double Duration)
{
    public double EndTime => StartTime + Duration;
}

/// <summary>The complete submitted step and the existing Job completion that covers its work.</summary>
public readonly record struct PhysicsStepResult(
    long Number,
    double StartTime,
    double EndTime,
    JobHandle Completion)
{
    public void Complete() => Completion.Complete();
}
