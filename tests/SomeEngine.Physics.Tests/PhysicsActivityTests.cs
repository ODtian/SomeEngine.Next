using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsActivityTests
{
    private struct SelectedConnection : IComponent;

    [Fact]
    public void AFilterCommandSplitsTheSleepingIslandBeforeTheSelectedStepMovesItsBody()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, Vector3.UnitX), second = CreateBody(world, Vector3.UnitX * 2), other = CreateBody(world, Vector3.UnitX * 10);
        foreach (Entity body in new[] { first, second, other }) world.Add(body, new BodyActivity { Radius = 1 });
        Entity connection = world.CreateRelation(first, second, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(connection, new SelectedConnection());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().All<SelectedConnection>().ReadWrite<XpbdLength>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .25f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.Read<BodyActivity>(second).Sleeping);
        using var commands = new JobCommandBuffer(world, 1);
        commands.Schedule(0, new RemoveConnectionSelection(connection, first));

        physics.Schedule(2, .25, .125, commands.SchedulePlayback()).Complete();

        Assert.Equal(.125f, world.Read<BodyPose>(first).Position.Y);
        Assert.Equal(0, world.Read<BodyPose>(second).Position.Y);
        Assert.False(world.Read<BodyActivity>(second).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }

    private readonly struct RemoveConnectionSelection(Entity connection, Entity body) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            commands.Remove<SelectedConnection>(connection);
            commands.Replace(body, new BodyVelocity { Linear = Vector3.UnitY });
        }
    }

    [Fact]
    public void ATimeSelectedWakeOnTheSharedAnchorWakesBothSleepingIslands()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        world.AddBuffer<BodyWake>(anchor);
        Entity first = CreateBody(world, Vector3.UnitX), second = CreateBody(world, -Vector3.UnitX);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(second, new BodyActivity { Radius = 1 });
        world.CreateRelation(anchor, first, new XpbdLength { RestLength = 1 });
        world.CreateRelation(anchor, second, new XpbdLength { RestLength = 1 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .25f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(second).Sleeping);
        world.ExecuteBufferWrite<BodyWake>(anchor, static requests => requests.Add(new BodyWake(.375)));
        physics.Schedule(2, .25, .125).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(second).Sleeping);

        physics.Schedule(3, .375, .125).Complete();

        Assert.False(world.Read<BodyActivity>(first).Sleeping); Assert.False(world.Read<BodyActivity>(second).Sleeping);
        world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ANewSelectedConstraintWakesOnlyItsConnectedSleepingIsland()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity first = CreateBody(world, Vector3.UnitX);
        Entity other = CreateBody(world, Vector3.UnitX * 10);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(other, new BodyActivity { Radius = 1 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .1).Complete(); physics.Schedule(1, .1, .1).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.CreateRelation(anchor, first, new XpbdLength { RestLength = 1 });

        physics.Schedule(2, .2, .1).Complete();

        Assert.False(world.Read<BodyActivity>(first).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ARemainingJointAngleKeepsTheMotionlessIslandAwake()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, Vector3.Zero);
        world.Replace(source, new BodyMass { Motion = BodyMotion.Static });
        Entity target = CreateBody(world, Vector3.Zero);
        world.Replace(target, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        world.Add(target, new BodyActivity { Radius = 1 });
        Entity joint = world.CreateRelation(source, target, JointConstraints.CreateFixed(JointFrame.Identity, JointFrame.Identity,
            float.PositiveInfinity, float.PositiveInfinity)).Entity;
        world.Add(joint, new AvbdJointState { LinearPenalty = new(1e6f), AngularPenalty = new(1e6f) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(joints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 0, 0, 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish());

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.False(world.Read<BodyActivity>(target).Sleeping);
        Assert.InRange(Quaternion.Dot(world.Read<BodyPose>(target).Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), .99999f, 1.00001f);
        world.ReleaseQuery(joints); world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ARemainingLengthErrorKeepsTheMotionlessIslandAwake()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, Vector3.UnitX * 3);
        world.Add(body, new BodyActivity { Radius = 1 });
        world.CreateRelation(anchor, body, new XpbdLength { RestLength = 1 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish());

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.False(world.Read<BodyActivity>(body).Sleeping);
        Assert.Equal(Vector3.UnitX * 3, world.Read<BodyPose>(body).Position);
        world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ActivePredictionFinishesWhileAnotherJobReadsTheSleepingPose()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sleeping = CreateBody(world, Vector3.UnitY * 10);
        Entity awake = CreateBody(world, Vector3.Zero);
        world.Add(sleeping, new BodyActivity { Sleeping = true, Radius = 1 });
        world.Add(awake, new BodyActivity { Radius = 1 });
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var predicted = new ManualResetEventSlim();
        var work = new BodyRead(poses, [sleeping]);
        JobHandle reader = EntityQueryJobs.Schedule(world, in work, new HoldBodyRead(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        var stages = prediction.Then(new SignalPrediction(predicted));
        PhysicsStepResult step = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1);
        try { Assert.True(predicted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); step.Complete(); reader.Complete(); }

        Assert.Equal(Vector3.UnitY * 10, world.Read<BodyPose>(sleeping).Position);
        Assert.InRange(world.Read<BodyPose>(awake).Position.Y, -.10001f, -.09999f);
        world.ReleaseQuery(bodies); world.ReleaseQuery(poses);
    }

    private readonly struct BodyRead(QueryHandle query, Entity[] entities) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => query;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => entities;
    }
    private readonly struct HoldBodyRead(ManualResetEventSlim entered, ManualResetEventSlim release) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            EntityQuery poses = queries.Get(0);
            poses.TryReadRef<BodyPose>(poses.Entities[0], out var pose);
            Assert.Equal(Vector3.UnitY * 10, pose.Value.Position);
            entered.Set(); release.Wait();
            Assert.Equal(Vector3.UnitY * 10, pose.Value.Position);
        }
    }
    private readonly struct SignalPrediction(ManualResetEventSlim predicted) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            WorldJobs.GetScheduler(context.World).Schedule(new Signal(predicted), dependency);
    }
    private readonly record struct Signal(ManualResetEventSlim Event) : IJob
    {
        public void Execute() => Event.Set();
    }

    [Fact]
    public void ANewContactResumesTheSleepingBodyPredictionInTheSameSubstep()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sphere = CreateBody(world, Vector3.UnitY);
        Entity plane = CreateBody(world, Vector3.Zero);
        world.Add(sphere, new BodyActivity { Sleeping = true, Radius = 1 });
        world.Replace(plane, new BodyMass { Motion = BodyMotion.Kinematic });
        world.Replace(plane, new BodyVelocity { Linear = Vector3.UnitY });
        world.Add(sphere, new SphereGeometry(1));
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(sphere, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(plane, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        QueryHandle planes = world.Query(new QueryDefinitionBuilder().All<PlaneGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        using var detected = new ContactPointBuffer(scheduler, 4, 8);
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
            new ConvexPlaneCollision<SphereGeometry>(1e-5f), new ContactPointOutput(detected), 0));
        using var detection = DetectContactPoints.Create(world, world.Between(spheres, planes).PartitionPairs(), bins,
            new SpherePlaneTypes(), narrow, detected, ReadOnlySpan<JobResourceAccess>.Empty);
        using var points = ContactPoints.Create(world, poses, scope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1e6f }), new ContactRefreshSettings(.02f, .99f, true));
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdGraphConstraints<ContactPoint, AvbdContactMath>(contacts),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 12, 0, 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, 1e-4f, 1e-4f));
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        var stages = prediction.Then(detection).Then(points.Refresh()).Then(activity.WakeConnected()).Then(prediction.Resume())
            .Then(new InitializeContactResponses(world, bodies, contacts)).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(11, 2, .1).Complete();

        Assert.False(world.Read<BodyActivity>(sphere).Sleeping);
        BodyStepState step = world.Read<BodyStepState>(sphere);
        Assert.Equal(Vector3.UnitY, step.StartCenterOfMass);
        Assert.InRange(step.PredictedCenterOfMass.Y, .89999f, .90001f);
        Assert.Equal(11, step.Number);
        Assert.Equal(2.1, step.Time);
        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, 1.0999f, 1.1001f);
        Assert.InRange(world.Read<BodyPose>(plane).Position.Y, .09999f, .10001f);
        world.ReleaseQuery(scope); world.ReleaseQuery(contacts); world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses); world.ReleaseQuery(planes); world.ReleaseQuery(spheres);
    }

    private readonly struct SpherePlaneTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 1);
    }

    [Fact]
    public void AnImpulseWakesItsQuietIslandWhileTheOtherIslandKeepsSleeping()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var moving = new Entity[4];
        for (int island = 0; island < 2; island++)
        {
            Vector3 offset = new(island * 10, 0, 0);
            Entity anchor = CreateBody(world, offset);
            world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
            Entity first = CreateBody(world, offset + Vector3.UnitX);
            Entity last = CreateBody(world, offset + Vector3.UnitX * 2);
            moving[island * 2] = first; moving[island * 2 + 1] = last;
            world.Add(first, new BodyActivity { Radius = 1 });
            world.Add(last, new BodyActivity { Radius = 1 });
            world.CreateRelation(anchor, first, new XpbdLength { RestLength = 1 });
            world.CreateRelation(first, last, new XpbdLength { RestLength = 1 });
        }
        world.AddBuffer<BodyImpulse>(moving[1]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>().Optional<BodyActivity>(QueryAccess.Read));
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 12);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, 1e-4f, 1e-4f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = new ApplyBodyInputs(64).Then(activity.WakeConnected()).Then(prediction).Then(solve)
            .Then(new UpdateBodyVelocities(64)).Then(activity.Finish());
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .1).Complete();
        physics.Schedule(1, .1, .1).Complete();
        foreach (Entity body in moving) Assert.True(world.Read<BodyActivity>(body).Sleeping);
        world.ExecuteBufferWrite<BodyImpulse>(moving[1], static impulses =>
            impulses.Add(new BodyImpulse { Linear = Vector3.UnitY, Time = .2 }));

        physics.Schedule(2, .2, .1).Complete();

        Assert.False(world.Read<BodyActivity>(moving[0]).Sleeping);
        Assert.False(world.Read<BodyActivity>(moving[1]).Sleeping);
        Assert.True(world.Read<BodyActivity>(moving[2]).Sleeping);
        Assert.True(world.Read<BodyActivity>(moving[3]).Sleeping);
        Assert.True(world.Read<BodyPose>(moving[1]).Position.Y > .05f);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void SleepingBodiesKeepTheirPoseAndReceiveTheCurrentSubstepTime()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity body = CreateBody(world, new Vector3(0, 10, 0));
        world.Add(body, new BodyActivity { Sleeping = true, Radius = 1 });
        uint poseVersion = world.WriteVersion<BodyPose>(body);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Optional<BodyActivity>(QueryAccess.Read));
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(joints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 2, 0, 0);
        var stages = prediction.Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 2).Schedule(7, 1, .2).Complete();

        Assert.Equal(new Vector3(0, 10, 0), world.Read<BodyPose>(body).Position);
        Assert.Equal(Vector3.Zero, world.Read<BodyVelocity>(body).Linear);
        Assert.Equal(poseVersion, world.WriteVersion<BodyPose>(body));
        BodyStepState step = world.Read<BodyStepState>(body);
        Assert.Equal(7, step.Number);
        Assert.Equal(1, step.Substep);
        Assert.InRange(Math.Abs(step.Time - 1.2), 0, 1e-12);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
