using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsGraphSolveTests
{
    [Fact]
    public void GraphParticipantsFollowTheReferencedArrayAfterItsProducerCompletes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var collected = new ManualResetEventSlim();
        Entity original = CreateBody(world, new Vector3(0, 5, 0), 1);
        Entity current = CreateBody(world, new Vector3(0, 7, 0), 1);
        Entity[] participants = [original];
        Entity target = world.CreateEntity(new ReferencedGraphTarget(participants, 1, collected));
        world.Add(target, new ReferencedGraphState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<ReferencedGraphTarget>().ReadWrite<ReferencedGraphState>());
        var resources = new QueryReadResources();
        resources.Add<ReferencedGraphTarget, ReferencedGraphResources>();
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdGraphConstraints<ReferencedGraphTarget, ReferencedGraphProjection>(targets),
            new(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1, resources);
        var ready = new TaskCompletionSource();
        JobHandle input = scheduler.CreateExternalFenceHandle(ready.Task);
        JobHandle producer = scheduler.Schedule(new SetGraphParticipant(participants, current), JobResourceAccess.Write(scheduler, participants), input);
        var stages = new PredictBodies(Vector3.Zero, 16).Then(solve).Then(new UpdateBodyVelocities(16));

        PhysicsStepResult step = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1);
        try { Assert.True(collected.Wait(TimeSpan.FromSeconds(10))); }
        finally { ready.SetResult(); }
        step.Complete();
        producer.Complete();

        Assert.Equal(new Vector3(0, 5, 0), world.Read<BodyPose>(original).Position);
        Assert.Equal(new Vector3(0, 1, 0), world.Read<BodyPose>(current).Position);
        Assert.Equal(new Vector3(0, -6, 0), world.Read<BodyVelocity>(current).Linear);
        world.ReleaseQuery(targets);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void IndependentIslandsShareTheFixedBodyWhileMixedProjectionsRunTogether()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var together = new Barrier(2);
        Entity fixedBody = CreateBody(world, Vector3.Zero, 0);
        Entity first = CreateBody(world, new Vector3(3, 2, 0), 1);
        Entity second = CreateBody(world, new Vector3(-3, 2, 0), 1);
        world.CreateRelation(fixedBody, first, new XpbdLength { RestLength = MathF.Sqrt(2) });
        world.CreateRelation(fixedBody, second, new XpbdLength { RestLength = MathF.Sqrt(2) });
        Entity firstHeight = world.CreateEntity(new IslandHeightTarget(first, 1, together));
        Entity secondHeight = world.CreateEntity(new IslandHeightTarget(second, 1, together));
        world.Add(firstHeight, new IslandHeightState());
        world.Add(secondHeight, new IslandHeightState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle heights = world.Query(new QueryDefinitionBuilder().Read<IslandHeightTarget>().ReadWrite<IslandHeightState>());
        var constraints = XpbdGraphConstraints.Combine(new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new XpbdGraphConstraints<IslandHeightTarget, IslandHeightProjection>(heights));
        var resources = new QueryReadResources();
        resources.Add<IslandHeightTarget, IslandHeightResources>();
        using var solve = SolveXpbdGraph.Create(world, bodies, constraints,
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, SmallIslandBodyLimit: 1, BodiesPerBatch: 1,
                LargeIslandBodyLimit: 32, RowsPerBlock: 16, UseSimd: false), iterations: 16, resources);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(1, 1, 0), world.Read<BodyPose>(first).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-1, 1, 0), world.Read<BodyPose>(second).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-2, -1, 0), world.Read<BodyVelocity>(first).Linear), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(fixedBody).Position);
        Assert.Equal(Vector3.Zero, world.Read<BodyVelocity>(fixedBody).Linear);
        world.ReleaseQuery(heights);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position, float inverseMass)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = inverseMass == 0 ? BodyMotion.Static : BodyMotion.Dynamic, InverseMass = inverseMass });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        return body;
    }
}

public readonly record struct ReferencedGraphTarget(Entity[] Bodies, float Height, ManualResetEventSlim Collected) : IComponent;
public struct ReferencedGraphState : IComponent { public float Multiplier; }
public readonly struct ReferencedGraphResources : IJobComponentResources<ReferencedGraphTarget>
{
    public static void Collect(in ReferencedGraphTarget component, ref JobResourceAccessBuilder resources)
    {
        resources.Read(component.Bodies);
        component.Collected.Set();
    }
}
public readonly struct ReferencedGraphProjection : IXpbdConstraint<ReferencedGraphTarget>, IPhysicsConstraintBodies<ReferencedGraphTarget>
{
    public static int GetBodyCount(in ReferencedGraphTarget constraint, scoped QueryRow row, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in ReferencedGraphTarget constraint, scoped QueryRow row, int index, in PhysicsStepContext context) => constraint.Bodies[index];
    public static void Initialize(in ReferencedGraphTarget constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<ReferencedGraphState>() = default;
    public static void Project(in ReferencedGraphTarget constraint, scoped QueryRow row, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        Entity body = constraint.Bodies[0];
        ref BodyPose pose = ref bodies.WritePose(body);
        row.ReadWrite<ReferencedGraphState>().Multiplier += constraint.Height - pose.Position.Y;
        pose.Position.Y = constraint.Height;
    }
}
public readonly struct SetGraphParticipant(Entity[] participants, Entity body) : IJob
{
    public void Execute() => participants[0] = body;
}

public readonly record struct IslandHeightTarget(Entity Body, float Height, Barrier Together) : IComponent;
public struct IslandHeightState : IComponent
{
    public float Multiplier;
    public bool Started;
}

public readonly struct IslandHeightResources : IJobComponentResources<IslandHeightTarget>
{
    public static void Collect(in IslandHeightTarget component, ref JobResourceAccessBuilder resources) { }
}

public readonly struct IslandHeightProjection : IXpbdConstraint<IslandHeightTarget>, IPhysicsConstraintBodies<IslandHeightTarget>
{
    public static int GetBodyCount(in IslandHeightTarget constraint, scoped QueryRow row, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in IslandHeightTarget constraint, scoped QueryRow row, int index, in PhysicsStepContext context) => constraint.Body;
    public static void Initialize(in IslandHeightTarget constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<IslandHeightState>() = default;
    public static void Project(in IslandHeightTarget constraint, scoped QueryRow row, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref IslandHeightState state = ref row.ReadWrite<IslandHeightState>();
        if (!state.Started)
        {
            if (!constraint.Together.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            state.Started = true;
        }
        ref BodyPose pose = ref bodies.WritePose(constraint.Body);
        float change = constraint.Height - pose.Position.Y;
        state.Multiplier += change / bodies.ReadMass(constraint.Body).InverseMass;
        pose.Position.Y += change;
    }
}
