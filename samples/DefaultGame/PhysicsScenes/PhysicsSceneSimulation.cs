using SomeEngine.Core.ECS;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Physics;

namespace DefaultGame;

public readonly record struct PhysicsSceneSettings(int Iterations, int BodyIterations, double StepBudgetMilliseconds);

internal readonly record struct PhysicsSceneEntities(Entity[] Bodies, Entity[] Joints, Entity[] DistanceRanges, bool HasInputChanges);

/// <summary>Scene references and its original frame completion; body and constraint values reside in the World.</summary>
public sealed class PhysicsSceneSimulation : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle[] _queries;
    private readonly IDisposable[] _resources;
    private readonly Func<PhysicsStepResult> _completeForRead;

    internal PhysicsSceneSimulation(World world, PhysicsSceneDescription description, PhysicsSceneSettings settings,
        Entity[] bodies, Entity[] joints, Entity[] distanceRanges, double stepSeconds, IGameFramePhysics frame, Func<PhysicsStepResult> completeForRead,
        QueryHandle[] queries, IDisposable[] resources)
    {
        _world = world; Description = description; Settings = settings; Bodies = bodies; Joints = joints; DistanceRanges = distanceRanges; StepSeconds = stepSeconds;
        Frame = frame; _completeForRead = completeForRead; _queries = queries; _resources = resources;
    }

    public PhysicsSceneDescription Description { get; }
    public PhysicsSceneSettings Settings { get; }
    public ReadOnlyMemory<Entity> Bodies { get; }
    public ReadOnlyMemory<Entity> Joints { get; }
    public ReadOnlyMemory<Entity> DistanceRanges { get; }
    public double StepSeconds { get; }
    public IGameFramePhysics Frame { get; }
    public PhysicsStepResult CompleteForRead() => _completeForRead();

    public void Dispose()
    {
        Frame.Dispose();
        for (int index = _resources.Length - 1; index >= 0; index--) _resources[index].Dispose();
        foreach (QueryHandle query in _queries) _world.ReleaseQuery(query);
    }
}
