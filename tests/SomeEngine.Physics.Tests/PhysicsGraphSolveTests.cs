using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsGraphSolveTests
{
    [Fact]
    public void ChangedBodiesAndConstraintsKeepTheirSelectionThroughPredictionAndGraphSolve()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero, 0);
        Entity body = CreateBody(world, new(2, 0, 0), 1);
        Relations<XpbdLength>.CreateEdge(world, anchor, body, new XpbdLength { RestLength = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().Changed<BodyPose>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>()
            .WithStored<RelationEndpoints<XpbdLength>>().Changed<XpbdLength>());
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero, 16);
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new(PhysicsGraphOrder.Colors, 0, 4, 1, 1, false), 1);
        var stages = prediction.Then(prediction.Resume()).Then(solve).Then(new UpdateBodyVelocities(16));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1).Complete();

        Assert.Equal(Vector3.UnitX, world.ReadComponentData<BodyPose>(body).Position);
        Assert.Equal(-Vector3.UnitX, world.ReadComponentData<BodyVelocity>(body).Linear);
    }

    [Fact]
    public void AvbdTargetOnlyMotorReadsTheDynamicSourceAfterItsLengthProjection()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity fixedBody = CreateBody(world, Vector3.Zero, 0);
        Entity source = CreateBody(world, new Vector3(2, 0, 0), 1);
        Entity target = CreateBody(world, new Vector3(3, 0, 0), 1);
        foreach (Entity body in new[] { fixedBody, source, target }) world.Add(body, new AvbdBodyEquation());
        Relations<XpbdLength>.CreateEdge(world, fixedBody, source, new XpbdLength { RestLength = 1 });
        var motor = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(motor.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            LinearStiffness = new(100, 0, 0),
            MaximumForce = new(100),
        });
        world.Add(motor.Entity, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity) });
        world.Add(motor.Entity, new XpbdMotorState());
        world.Add(motor.Entity, new AvbdMotorState { LinearPenalty = new(1000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query xpbdMotors = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<JointMotor>().WithStored<JointMotorTarget>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<XpbdMotorState>());
        Query avbdMotors = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<JointMotor>().WithStored<JointMotorTarget>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<AvbdMotorState>());
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Colors, 0, 4, 1, 1, false);
        using var lengthSolve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths), settings, 1);
        using var solve = SolveAvbdGraph.Create(world, bodies, AvbdGraphConstraints.Combine(
            new XpbdForces<XpbdLength, XpbdLengthProjection>(lengths),
            new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(avbdMotors)), settings, 32, 0, 0);
        var stages = new PredictBodies(Vector3.Zero, 16).Then(lengthSolve).Then(solve).Then(new UpdateBodyVelocities(16));
        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1).Complete();



        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2.009901f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(-.990099f, 0, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-5f);
    }

    [Fact]
    public void XpbdTargetOnlyMotorReadsTheDynamicSourceAfterItsLengthProjection()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity fixedBody = CreateBody(world, Vector3.Zero, 0);
        Entity source = CreateBody(world, new Vector3(2, 0, 0), 1);
        Entity target = CreateBody(world, new Vector3(3, 0, 0), 1);
        foreach (Entity body in new[] { fixedBody, source, target }) world.Add(body, new AvbdBodyEquation());
        Relations<XpbdLength>.CreateEdge(world, fixedBody, source, new XpbdLength { RestLength = 1 });
        var motor = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(motor.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            LinearStiffness = new(100, 0, 0),
            MaximumForce = new(100),
        });
        world.Add(motor.Entity, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity) });
        world.Add(motor.Entity, new XpbdMotorState());
        world.Add(motor.Entity, new AvbdMotorState { LinearPenalty = new(1000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query xpbdMotors = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<JointMotor>().WithStored<JointMotorTarget>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<XpbdMotorState>());
        Query avbdMotors = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<JointMotor>().WithStored<JointMotorTarget>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<AvbdMotorState>());
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Colors, 0, 4, 1, 1, false);
        using var lengthSolve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths), settings, 1);
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(xpbdMotors), settings, 1);
        var stages = new PredictBodies(Vector3.Zero, 16).Then(lengthSolve).Then(solve).Then(new UpdateBodyVelocities(16));
        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1).Complete();



        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2.009901f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(-.990099f, 0, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-5f);
    }

    [Fact]
    public void GraphParticipantsFollowTheReferencedArrayAfterItsProducerCompletes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        using var collected = new ManualResetEventSlim();
        Entity original = CreateBody(world, new Vector3(0, 5, 0), 1);
        Entity current = CreateBody(world, new Vector3(0, 7, 0), 1);
        Entity[] participants = [original];
        Entity target = world.Create(new ReferencedGraphTarget(participants, 1, collected));
        world.Add(target, new ReferencedGraphState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query targets = world.Query(new QueryDescription().WithStored<ReferencedGraphTarget>().WithStored<ReferencedGraphState>());
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<ReferencedGraphTarget, ReferencedGraphProjection>(targets),
            new(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        var ready = new TaskCompletionSource();
        JobHandle input = scheduler.CreateExternalFenceHandle(ready.Task);
        JobHandle producer = scheduler.Schedule(new SetGraphParticipant(participants, current), JobResourceAccess.Write(scheduler, participants), input);
        var stages = new PredictBodies(Vector3.Zero, 16).Then(solve).Then(new UpdateBodyVelocities(16));

        PhysicsStepResult step = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1);
        try { Assert.True(collected.Wait(TimeSpan.FromSeconds(10))); }
        finally { ready.SetResult(); }
        step.Complete();
        producer.Complete();

        Assert.Equal(new Vector3(0, 5, 0), world.ReadComponentData<BodyPose>(original).Position);
        Assert.Equal(new Vector3(0, 1, 0), world.ReadComponentData<BodyPose>(current).Position);
        Assert.Equal(new Vector3(0, -6, 0), world.ReadComponentData<BodyVelocity>(current).Linear);
    }

    [Fact]
    public void IndependentIslandsShareTheFixedBodyWhileMixedProjectionsRunTogether()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        using var together = new Barrier(2);
        Entity fixedBody = CreateBody(world, Vector3.Zero, 0);
        Entity first = CreateBody(world, new Vector3(3, 2, 0), 1);
        Entity second = CreateBody(world, new Vector3(-3, 2, 0), 1);
        Relations<XpbdLength>.CreateEdge(world, fixedBody, first, new XpbdLength { RestLength = MathF.Sqrt(2) });
        Relations<XpbdLength>.CreateEdge(world, fixedBody, second, new XpbdLength { RestLength = MathF.Sqrt(2) });
        Entity firstHeight = world.Create(new IslandHeightTarget(first, 1, together));
        Entity secondHeight = world.Create(new IslandHeightTarget(second, 1, together));
        world.Add(firstHeight, new IslandHeightState());
        world.Add(secondHeight, new IslandHeightState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query heights = world.Query(new QueryDescription().WithStored<IslandHeightTarget>().WithStored<IslandHeightState>());
        var constraints = XpbdGraphConstraints.Combine(new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new XpbdConstraints<IslandHeightTarget, IslandHeightProjection>(heights));
        using var solve = SolveXpbdGraph.Create(world, bodies, constraints,
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, SmallIslandBodyLimit: 1, BodiesPerBatch: 1,
                LargeIslandBodyLimit: 32, RowsPerBlock: 16, UseSimd: false), iterations: 16);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(1, 1, 0), world.ReadComponentData<BodyPose>(first).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-1, 1, 0), world.ReadComponentData<BodyPose>(second).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-2, -1, 0), world.ReadComponentData<BodyVelocity>(first).Linear), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(fixedBody).Position);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyVelocity>(fixedBody).Linear);
    }

    private static Entity CreateBody(World world, Vector3 position, float inverseMass)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
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
public readonly struct ReferencedGraphProjection : IXpbdConstraint<ReferencedGraphTarget>
{
    private readonly GraphInputFields<ReferencedGraphState> _fields;
    public ReferencedGraphProjection() => _fields = new();
    private static Query Source(World world) => world.Query(new QueryDescription().With<ReferencedGraphTarget>());
    public static void AddInputAccesses(World world, Query members, List<JobResourceAccess> accesses) =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess<ReadInput>(Source(world)));
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<ReferencedGraphState, ReferencedGraphState>.Accesses(world, members));
        accesses.AddRange(((WorldJobs)world.Execution!).CollectReadAccess<ReferencedGraphTarget, ReferencedGraphResources>(Source(world)));
    }
    public void Bind(World world, Query members) => _fields.Bind(world, members);
    public ref readonly ReferencedGraphTarget ReadConstraint(Entity entity) => ref _fields.World.Read<ReferencedGraphTarget>(entity);
    public static int GetBodyCount(in ReferencedGraphTarget constraint, World world, Entity entity, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in ReferencedGraphTarget constraint, World world, Entity entity, int index, in PhysicsStepContext context) => constraint.Bodies[index];
    public void Initialize(in ReferencedGraphTarget constraint, World world, Entity entity, in PhysicsStepContext context) => _fields.States.WriteState(entity) = default;
    public void Project(in ReferencedGraphTarget constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        Entity body = constraint.Bodies[0]; ref BodyPose pose = ref bodies.WritePose(body);
        _fields.States.WriteState(entity).Multiplier += constraint.Height - pose.Position.Y; pose.Position.Y = constraint.Height;
    }
    private readonly struct ReadInput : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.ReadComponentData<ReferencedGraphTarget>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows) { }
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

public readonly struct IslandHeightProjection : IXpbdConstraint<IslandHeightTarget>
{
    private readonly GraphInputFields<IslandHeightState> _fields;
    public IslandHeightProjection() => _fields = new();
    private static Query Source(World world) => world.Query(new QueryDescription().With<IslandHeightTarget>());
    public static void AddInputAccesses(World world, Query members, List<JobResourceAccess> accesses) =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess<ReadInput>(Source(world)));
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(ConstraintColumns<IslandHeightState, IslandHeightState>.Accesses(world, members));
        accesses.AddRange(((WorldJobs)world.Execution!).CollectReadAccess<IslandHeightTarget, IslandHeightResources>(Source(world)));
    }
    public void Bind(World world, Query members) => _fields.Bind(world, members);
    public ref readonly IslandHeightTarget ReadConstraint(Entity entity) => ref _fields.World.Read<IslandHeightTarget>(entity);
    public static int GetBodyCount(in IslandHeightTarget constraint, World world, Entity entity, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in IslandHeightTarget constraint, World world, Entity entity, int index, in PhysicsStepContext context) => constraint.Body;
    public void Initialize(in IslandHeightTarget constraint, World world, Entity entity, in PhysicsStepContext context) => _fields.States.WriteState(entity) = default;
    public void Project(in IslandHeightTarget constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref IslandHeightState state = ref _fields.States.WriteState(entity);
        if (!state.Started)
        {
            if (!constraint.Together.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            state.Started = true;
        }
        ref BodyPose pose = ref bodies.WritePose(constraint.Body);
        float change = constraint.Height - pose.Position.Y;
        state.Multiplier += change / bodies.ReadMass(constraint.Body).InverseMass; pose.Position.Y += change;
    }
    private readonly struct ReadInput : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.ReadComponentData<IslandHeightTarget>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows) { }
    }
}
internal sealed class GraphInputFields<TState> where TState : unmanaged
{
    internal World World = null!;
    internal readonly ConstraintColumns<TState, TState> States = new();
    internal void Bind(World world, Query members) { World = world; States.Bind(world, members); }
}
