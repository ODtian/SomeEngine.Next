using SomeEngine.Core.ECS;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

public readonly record struct PhysicsSceneSettings(int Iterations, int BodyIterations, double StepBudgetMilliseconds)
{
    public BodyActivitySettings Activity { get; init; } = new(.05f, .5f, .001f, .001f);
    public JointLongRangeSettings JointBounds { get; init; } = new(0, 32, .99f, .99f);
    public int JointBoundsPerJoint { get; init; }
    public ContactSupportSettings? ContactSupports { get; init; }
    public float MultiplierRetention { get; init; } = .8f;
    public float FrictionRegularizationSpeed { get; init; } = .01f;
    public float ContactNormalPenalty { get; init; } = 1e6f;
    public float ContactTangentPenalty { get; init; } = 1e6f;
    public PhysicsGraphSettings Graph { get; init; } = new(PhysicsGraphOrder.Islands, 16, 16, 64, 4, false);
    public float ContactDistance { get; init; }
    public bool GeometryInputsFixed { get; init; }
    public bool EquationBindingsFixed { get; init; }
    public double MaximumResultDelay { get; init; }
    public PhysicsSceneTimings? Timings { get; init; }
    public bool ReadContacts { get; init; }
}

internal readonly record struct PhysicsSceneEntities(Entity[] Bodies, Entity[] Joints, Entity[] DistanceRanges, bool HasInputChanges);

/// <summary>Scene references and its original frame completion; body and constraint values reside in the World.</summary>
public sealed class PhysicsSceneSimulation : IGameFramePhysics
{
    private readonly World _world;
    private readonly Query[] _queries;
    private readonly IDisposable[] _resources;
    private readonly ContactColumns? _contactColumns;

    internal PhysicsSceneSimulation(World world, PhysicsSceneDescription description, PhysicsSceneSettings settings,
        Entity[] bodies, Entity[] joints, Entity[] distanceRanges, double stepSeconds, IPhysicsFrame frame,
        Query[] queries, IDisposable[] resources, DeviceSceneStatistics? deviceStatistics = null, Entity[]? readbackBodies = null,
        PhysicsContacts? contacts = null, ContactColumns? contactColumns = null)
    {
        _world = world; Description = description; Settings = settings; Bodies = bodies; Joints = joints; DistanceRanges = distanceRanges; StepSeconds = stepSeconds;
        Frame = frame; _queries = queries; _resources = resources;
        DeviceStatistics = deviceStatistics;
        _contactColumns = contactColumns; Contacts = contacts ?? contactColumns?.Contacts;
        ReadbackBodies = readbackBodies ?? bodies;
    }

    public PhysicsContacts? Contacts { get; }
    public PhysicsSceneDescription Description { get; }
    public PhysicsSceneSettings Settings { get; }
    public ReadOnlyMemory<Entity> Bodies { get; }
    public ReadOnlyMemory<Entity> ReadbackBodies { get; }
    public ReadOnlyMemory<Entity> Joints { get; }
    public ReadOnlyMemory<Entity> DistanceRanges { get; }
    public double StepSeconds { get; }
    internal DeviceSceneStatistics? DeviceStatistics { get; }
    public int? DeviceAwakeBodies => DeviceStatistics?.AwakeBodies;
    public int? DeviceContactPoints => DeviceStatistics?.ContactPoints;
    public int? DeviceLargestIslandBodies => DeviceStatistics?.LargestIslandBodies;
    public IPhysicsFrame Frame { get; }
    public JobHandle Completion => Frame.Completion;
    public void AdvanceTime(double deltaTime) => Frame.AdvanceTime(deltaTime);
    public JobHandle ConsumeCompleted()
    {
        JobHandle completed = Frame.ConsumeCompleted();
        PhysicsStepResult result = Frame.Result with { Completion = completed };
        _contactColumns?.SetStep(in result);
        return completed;
    }
    public void SubmitDueSteps() => Frame.SubmitDueSteps();
    public PhysicsStepResult CompleteForRead()
    {
        PhysicsStepResult result = Frame.CompleteForRead();
        _contactColumns?.Publish(in result);
        return result;
    }

    public void Dispose()
    {
        Frame.Dispose();
        for (int index = _resources.Length - 1; index >= 0; index--) _resources[index].Dispose();
    }
}
