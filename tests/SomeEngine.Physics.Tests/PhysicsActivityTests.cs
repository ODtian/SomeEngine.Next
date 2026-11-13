using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsActivityTests
{
    private struct SelectedConnection : ITag;

    [Fact]
    public void AFilterCommandSplitsTheSleepingIslandBeforeTheSelectedStepMovesItsBody()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity first = CreateBody(world, Vector3.UnitX), second = CreateBody(world, Vector3.UnitX * 2), other = CreateBody(world, Vector3.UnitX * 10);
        foreach (Entity body in new[] { first, second, other }) world.Add(body, new BodyActivity { Radius = 1 });
        Entity connection = Relations<XpbdLength>.CreateEdge(world, first, second, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(connection, new SelectedConnection());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().With<SelectedConnection>().WithStored<XpbdLength>()
            .WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .25f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(second).Sleeping);
        using var commands = new CommandBuffer(world);
        commands.Remove<SelectedConnection>(connection); commands.Set(first, new BodyVelocity { Linear = Vector3.UnitY });
        JobHandle edited = scheduler.Schedule(new Replay(commands), ((WorldJobs)world.Execution!).StructureWrite);

        physics.Schedule(2, .25, .125, edited).Complete();

        Assert.Equal(.125f, world.ReadComponentData<BodyPose>(first).Position.Y);
        Assert.Equal(0, world.ReadComponentData<BodyPose>(second).Position.Y);
        Assert.False(world.ReadComponentData<BodyActivity>(second).Sleeping);
        Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
    }

    private readonly struct Replay(CommandBuffer commands) : IJob { public void Execute() => commands.Playback(); }

    [Fact]
    public void ATimeSelectedWakeOnTheSharedAnchorWakesBothSleepingIslands()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        world.AddBuffer<BodyWake>(anchor);
        Entity first = CreateBody(world, Vector3.UnitX), second = CreateBody(world, -Vector3.UnitX);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(second, new BodyActivity { Radius = 1 });
        Relations<XpbdLength>.CreateEdge(world, anchor, first, new XpbdLength { RestLength = 1 });
        Relations<XpbdLength>.CreateEdge(world, anchor, second, new XpbdLength { RestLength = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .25f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(second).Sleeping);
        using (var requests = world.GetBuffer<BodyWake>(anchor)) requests.Add(new BodyWake(.375));
        physics.Schedule(2, .25, .125).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(second).Sleeping);

        physics.Schedule(3, .375, .125).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.False(world.ReadComponentData<BodyActivity>(second).Sleeping);
    }

    [Fact]
    public void ANewSelectedConstraintWakesOnlyItsConnectedSleepingIsland()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity first = CreateBody(world, Vector3.UnitX);
        Entity other = CreateBody(world, Vector3.UnitX * 10);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(other, new BodyActivity { Radius = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 4);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()), 1);
        physics.Schedule(0, 0, .1).Complete(); physics.Schedule(1, .1, .1).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        Relations<XpbdLength>.CreateEdge(world, anchor, first, new XpbdLength { RestLength = 1 });

        physics.Schedule(2, .2, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(first).Sleeping);
        Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
    }

    [Fact]
    public void ARemainingJointAngleKeepsTheMotionlessIslandAwake()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, Vector3.Zero);
        world.Replace(source, new BodyMass { Motion = BodyMotion.Static });
        Entity target = CreateBody(world, Vector3.Zero);
        world.Replace(target, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        world.Add(target, new BodyActivity { Radius = 1 });
        Entity joint = Relations<JointConstraint>.CreateEdge(world, source, target, JointConstraints.CreateFixed(JointFrame.Identity, JointFrame.Identity,
            float.PositiveInfinity, float.PositiveInfinity)).Entity;
        world.Add(joint, new AvbdJointState { LinearPenalty = new(1e6f), AngularPenalty = new(1e6f) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<AvbdJointState>());
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(joints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 0, 0, 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish());

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(target).Sleeping);
        Assert.InRange(Quaternion.Dot(world.ReadComponentData<BodyPose>(target).Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), .99999f, 1.00001f);
    }

    [Fact]
    public void ARemainingLengthErrorKeepsTheMotionlessIslandAwake()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, Vector3.UnitX * 3);
        world.Add(body, new BodyActivity { Radius = 1 });
        Relations<XpbdLength>.CreateEdge(world, anchor, body, new XpbdLength { RestLength = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish());

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(body).Sleeping);
        Assert.Equal(Vector3.UnitX * 3, world.ReadComponentData<BodyPose>(body).Position);
    }

    [Fact]
    public void ActivePredictionFinishesWhileAnotherJobReadsTheSleepingPose()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity sleeping = CreateBody(world, Vector3.UnitY * 10);
        Entity awake = CreateBody(world, Vector3.Zero);
        world.Add(sleeping, new BodyActivity { Sleeping = true, Radius = 1 });
        world.Add(awake, new BodyActivity { Radius = 1 });
        SharedId<PoseReadGroup> readGroup = world.InternShared(new PoseReadGroup(1));
        world.SetShared(sleeping, readGroup); world.SetShared(awake, world.InternShared(new PoseReadGroup(0)));
        Query poses = world.Query(new QueryDescription().WithStored<BodyPose>().WithShared(readGroup));
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var predicted = new ManualResetEventSlim();
        JobHandle reader = new SystemRunner(world).Schedule(poses, new HoldBodyRead(entered, release), parallel: false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        var stages = prediction.Then(new SignalPrediction(predicted));
        PhysicsStepResult step = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, .1);
        try { Assert.True(predicted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); step.Complete(); reader.Complete(); }

        Assert.Equal(Vector3.UnitY * 10, world.ReadComponentData<BodyPose>(sleeping).Position);
        Assert.InRange(world.ReadComponentData<BodyPose>(awake).Position.Y, -.10001f, -.09999f);
    }

    private readonly record struct PoseReadGroup(int Value) : ISharedComponent;
    private readonly struct HoldBodyRead(ManualResetEventSlim entered, ManualResetEventSlim release) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<BodyPose>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<BodyPose> poses = rows.Read<BodyPose>();
            Assert.Equal(Vector3.UnitY * 10, poses[0].Position);
            entered.Set(); release.Wait();
            Assert.Equal(Vector3.UnitY * 10, poses[0].Position);
        }
    }
    private readonly struct SignalPrediction(ManualResetEventSlim predicted) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            (context.World.Execution!.Scheduler!).Schedule(new Signal(predicted), dependency);
    }
    private readonly record struct Signal(ManualResetEventSlim Event) : IJob
    {
        public void Execute() => Event.Set();
    }

    [Fact]
    public void ANewContactResumesTheSleepingBodyPredictionInTheSameSubstep()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity sphere = CreateBody(world, Vector3.UnitY);
        Entity plane = CreateBody(world, Vector3.Zero);
        world.Add(sphere, new BodyActivity { Sleeping = true, Radius = 1 });
        world.Replace(plane, new BodyMass { Motion = BodyMotion.Kinematic });
        world.Replace(plane, new BodyVelocity { Linear = Vector3.UnitY });
        world.Add(sphere, new SphereGeometry(1));
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(sphere, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(plane, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        Query spheres = world.Query(new QueryDescription().With<SphereGeometry>());
        Query planes = world.Query(new QueryDescription().With<PlaneGeometry>());
        Query poses = world.Query(new QueryDescription().WithStored<BodyPose>());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        using var detected = new ContactPointBuffer(scheduler, 4, 8);
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
            new ConvexPlaneCollision<SphereGeometry>(1e-5f), new ContactPointOutput(detected), 0));
        var detection = DetectContactPoints.Create(world, world.Between(spheres, planes).PartitionPairs(), bins,
            new SpherePlaneTypes(), narrow, detected, ReadOnlySpan<JobResourceAccess>.Empty);
        using var contacts = new ContactManifolds<AvbdContactState>(scheduler, new AvbdContactState { NormalPenalty = 1e6f });
        var refresh = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies, contacts,
            detected, new(), new(.02f, .99f, true));
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 12, 0, 0);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, 1e-4f, 1e-4f));
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        var stages = prediction.Then(detection).Then(refresh).Then(activity.WakeConnected()).Then(prediction.Resume())
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns)).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(11, 2, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(sphere).Sleeping);
        BodyStepState step = world.ReadComponentData<BodyStepState>(sphere);
        Assert.Equal(Vector3.UnitY, step.StartCenterOfMass);
        Assert.InRange(step.PredictedCenterOfMass.Y, .89999f, .90001f);
        Assert.Equal(11, step.Number);
        Assert.Equal(2.1, step.Time);
        Assert.InRange(world.ReadComponentData<BodyPose>(sphere).Position.Y, 1.0999f, 1.1001f);
        Assert.InRange(world.ReadComponentData<BodyPose>(plane).Position.Y, .09999f, .10001f);
    }

    private readonly struct SpherePlaneTypes : ICollisionPairClassifier
    {
        public QueryDescription AddReads(QueryDescription definition) => definition;
        public CollisionPairType Classify(World world, Entity first, Entity second) => new(0, 0, 1);
    }

    [Fact]
    public void AnImpulseWakesItsQuietIslandWhileTheOtherIslandKeepsSleeping()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
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
            Relations<XpbdLength>.CreateEdge(world, anchor, first, new XpbdLength { RestLength = 1 });
            Relations<XpbdLength>.CreateEdge(world, first, last, new XpbdLength { RestLength = 1 });
        }
        world.AddBuffer<BodyImpulse>(moving[1]);
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>().Optional<BodyActivity>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 12);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .2f, 1e-4f, 1e-4f));
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var stages = new ApplyBodyInputs(64).Then(activity.WakeConnected()).Then(prediction).Then(solve)
            .Then(new UpdateBodyVelocities(64)).Then(activity.Finish());
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);
        physics.Schedule(0, 0, .1).Complete();
        physics.Schedule(1, .1, .1).Complete();
        foreach (Entity body in moving) Assert.True(world.ReadComponentData<BodyActivity>(body).Sleeping);
        using (var impulses = world.GetBuffer<BodyImpulse>(moving[1])) impulses.Add(new BodyImpulse { Linear = Vector3.UnitY, Time = .2 });

        physics.Schedule(2, .2, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(moving[0]).Sleeping);
        Assert.False(world.ReadComponentData<BodyActivity>(moving[1]).Sleeping);
        Assert.True(world.ReadComponentData<BodyActivity>(moving[2]).Sleeping);
        Assert.True(world.ReadComponentData<BodyActivity>(moving[3]).Sleeping);
        Assert.True(world.ReadComponentData<BodyPose>(moving[1]).Position.Y > .05f);
    }

    [Fact]
    public void SleepingBodiesKeepTheirPoseAndReceiveTheCurrentSubstepTime()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity body = CreateBody(world, new Vector3(0, 10, 0));
        Quaternion rotation = Quaternion.Normalize(new Quaternion(.1f, .2f, .3f, .9f));
        BodyPose pose = world.ReadComponentData<BodyPose>(body);
        pose.Rotation = rotation;
        world.Replace(body, pose);
        world.Add(body, new BodyActivity { Sleeping = true, Radius = 1 });
        ulong poseVersion = world.ComponentDataWriteVersion<BodyPose>(body);
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>().Optional<BodyActivity>());
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<AvbdJointState>());
        using var prediction = new PredictActiveBodies(world, bodies, new Vector3(0, -10, 0));
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(joints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 2, 0, 0);
        var stages = prediction.Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 2).Schedule(7, 1, .2).Complete();

        Assert.Equal(new Vector3(0, 10, 0), world.ReadComponentData<BodyPose>(body).Position);
        Assert.Equal(rotation, world.ReadComponentData<BodyPose>(body).Rotation);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyVelocity>(body).Linear);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyVelocity>(body).Angular);
        Assert.Equal(poseVersion, world.ComponentDataWriteVersion<BodyPose>(body));
        BodyStepState step = world.ReadComponentData<BodyStepState>(body);
        Assert.Equal(7, step.Number);
        Assert.Equal(1, step.Substep);
        Assert.InRange(Math.Abs(step.Time - 1.2), 0, 1e-12);
    }

    private static Entity CreateBody(World world, Vector3 position)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
