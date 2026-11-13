using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CompletedPhysicsEventsTests
{

    [Fact]
    public void AnXpbdConnectionBreaksUsingForceUnitsFromItsCurrentSubstepMultiplier()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitX);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity connection = Relations<XpbdLength>.CreateEdge(world, anchor, body, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(connection, new ConstraintBreakSettings(3, float.PositiveInfinity));
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<ConstraintBreakSettings>());
        using var output = new PhysicsEventBuffer(world, 4);
        using var breaking = new BreakConstraints<XpbdLength, XpbdLengthProjection>(world, bodies, lengths, output);
        var solve = SolveXpbd.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths), 1);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var reader = output.Watch();
        var consumer = new RecordEvents(reader, received);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            new PredictBodies(Vector3.UnitX * 4, 64).Then(solve).Then(new UpdateBodyVelocities(64)).Then(breaking).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .25, 0, 0, 0);

        frame.AdvanceTime(.25); frame.CompleteForRead();

        var item = Assert.Single(received);
        Assert.Equal(connection, item.Event.Subject); Assert.Equal(anchor, item.Event.Body);
        Assert.Equal(new Vector3(4, 0, 0), item.Event.Force);
        Assert.Equal(Vector3.UnitX, world.ReadComponentData<BodyPose>(body).Position);
        frame.AdvanceTime(.25); frame.CompleteForRead();
        Assert.Equal(new Vector3(1.25f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
    }


    [Fact]
    public void AJointBreakingAtItsBodyForceLimitReleasesTheNextStepAndDeliversItsLoad()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitX);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity joint = Relations<JointConstraint>.CreateEdge(world, anchor, body, JointConstraints.CreateFixed(JointFrame.Identity,
            new JointFrame(-Vector3.UnitX, Quaternion.Identity), float.PositiveInfinity, float.PositiveInfinity)).Entity;
        world.Add(joint, new AvbdJointState { LinearPenalty = new(1e6f), AngularPenalty = new(1e6f) });
        world.Add(joint, new ConstraintBreakSettings(4, float.PositiveInfinity));
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<RelationEndpoints<JointConstraint>>()
            .WithStored<AvbdJointState>().WithStored<ConstraintBreakSettings>());
        using var output = new PhysicsEventBuffer(world, 4);
        using var breaking = new BreakConstraints<JointConstraint, AvbdJointMath>(world, bodies, joints, output);
        var solve = SolveAvbd.Create(world, bodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(joints), 48, 0, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var reader = output.Watch();
        var consumer = new RecordEvents(reader, received);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            new PredictBodies(new Vector3(0, -8, 0), 64).Then(solve).Then(new UpdateBodyVelocities(64)).Then(breaking).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);

        frame.AdvanceTime(.125); frame.CompleteForRead();

        using (var edges = Relations<JointConstraint>.Outgoing(world, anchor)) Assert.Equal(0, edges.Count);
        var item = Assert.Single(received);
        Assert.Equal(PhysicsEventKind.Break, item.Event.Kind); Assert.Equal(joint, item.Event.Subject); Assert.Equal(0, item.Number);
        Assert.Equal(anchor, item.Event.First); Assert.Equal(body, item.Event.Second);
        Assert.Equal(anchor, item.Event.Body);
        Assert.InRange(MathF.Abs(item.Event.Force.Y), 7.9f, 8.1f);
        float supportedHeight = world.ReadComponentData<BodyPose>(body).Position.Y;
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y - supportedHeight, -.1251f, -.1249f);
    }


    [Fact]
    public void ATriggerReportsEntryAndExitWhileTheBodyPassesThroughItsPlane()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitY * .125f);
        world.Replace(floor, new BodyMass { Motion = BodyMotion.Static });
        world.Replace(body, new BodyVelocity { Linear = -Vector3.UnitY * 2 });
        using var contacts = CreatePoint(scheduler, world, floor, body, Vector3.Zero, .125f);
        ContactTestData.SetFlags(scheduler, contacts, 0, ContactPointFlags.Trigger);
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>());
        using var output = new PhysicsEventBuffer(world, 4);
        using var observation = new ContactEvents(world, bodies, contacts.Contacts, output, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var reader = output.Watch();
        var consumer = new RecordEvents(reader, received);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            new PredictBodies(Vector3.Zero, 64).Then(new UpdateBodyVelocities(64)).Then(observation).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.Equal(-.125f, world.ReadComponentData<BodyPose>(body).Position.Y);
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitY * 2 });

        frame.AdvanceTime(.125); frame.CompleteForRead();

        Assert.Equal(.125f, world.ReadComponentData<BodyPose>(body).Position.Y);
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.All(received, item => { Assert.Equal(PhysicsEventKind.Trigger, item.Event.Kind); Assert.Equal(Vector3.Zero, item.Event.Force); });
        Assert.Equal(-.125f, received[0].Event.Separation);
        Assert.Equal(.125f, received[1].Event.Separation);
    }


    [Fact]
    public void ContactEventsFollowTheSolvedPointAndItsGeometryDestruction()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitY);
        world.Replace(floor, new BodyMass { Motion = BodyMotion.Static });
        using var contacts = CreatePoint(scheduler, world, floor, body, -Vector3.UnitY, 0);
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        using var output = new PhysicsEventBuffer(world, 8);
        using var observation = new ContactEvents(world, bodies, contacts.Contacts, output, .001f);
        var solve = SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 0, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var reader = output.Watch();
        var consumer = new RecordEvents(reader, received);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            new PredictBodies(new Vector3(0, -8, 0), 64).Then(new InitializeContactResponses(world, bodies, contacts.Columns)).Then(solve)
                .Then(new UpdateBodyVelocities(64)).Then(observation).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y, .9999f, 1.0001f);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        world.Destroy(floor);
        ContactTestData.Clear(scheduler, contacts);

        frame.AdvanceTime(.125); frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, received.Select(item => item.Number));
        Assert.All(received, item =>
        {
            Assert.Equal(PhysicsEventKind.Contact, item.Event.Kind); Assert.Equal(0u, item.Event.FirstShape); Assert.Equal(0u, item.Event.SecondShape);
            Assert.Equal(floor, item.Event.First); Assert.Equal(body, item.Event.Second);
        });
        Assert.InRange(received[0].Event.Force.Y, 7.98f, 8.02f);
        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y, .8749f, .8751f);
        Assert.Equal(0, contacts.Columns.PointCount);
    }

    [Fact]
    public void TheFrameConsumerReceivesAnIntermediateStepsSleepEventBeforeAcknowledgingIt()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity body = CreateBody(world, Vector3.Zero);
        world.Add(body, new BodyActivity { Radius = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var output = new PhysicsEventBuffer(world, 4);
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f), output);
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var reader = output.Watch();
        var consumer = new RecordEvents(reader, received);
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()),
            activity.WakeConnected().Then(prediction).Then(solve).Then(new UpdateBodyVelocities(64)).Then(activity.Finish()).Then(output.Emit()), 2);
        using var frame = physics.CreateFrame(consumer, .1, 1, 0, 0);
        frame.AdvanceTime(.31);

        PhysicsStepResult result = frame.CompleteForRead();

        Assert.Equal(2, result.Number);
        var item = Assert.Single(received);
        Assert.Equal(0, item.Number);
        Assert.Equal(PhysicsEventKind.Sleep, item.Event.Kind);
        Assert.Equal(body, item.Event.Subject);
        Assert.Equal(1, item.Event.Substep);
        Assert.Equal(.1, item.Event.Time);
        Assert.True(world.ReadComponentData<BodyActivity>(body).Sleeping);
        using (var consumed = reader.Open()) Assert.Equal(0, consumed.Count);
        double wakeTime = result.EndTime;
        world.AddBuffer<BodyWake>(body);
        using (var requests = world.GetBuffer<BodyWake>(body)) requests.Add(new BodyWake(wakeTime));
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.Equal(3, received.Count);
        Assert.Equal((3L, PhysicsEventKind.Wake), (received[1].Number, received[1].Event.Kind));
        Assert.Equal(wakeTime, received[1].Event.Time);
        Assert.Equal(0, received[1].Event.Substep);
        Assert.Equal((3L, PhysicsEventKind.Sleep), (received[2].Number, received[2].Event.Kind));
    }

    private readonly struct RecordEvents(EventReader<PhysicsEvent> reader, List<(long Number, PhysicsEvent Event)> results) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            (context.World.Execution!.Scheduler!).Schedule(new Capture((context.World.Execution!.Scheduler!), reader, results), dependency: dependency);
        private readonly struct Capture(Scheduler scheduler, EventReader<PhysicsEvent> reader, List<(long Number, PhysicsEvent Event)> results) : IJob
        {
            public void Execute()
            {
                EventReadRange range = reader.Capture();
                scheduler.Schedule(new Read(reader, range, results), [range.Read, JobResourceAccess.Write(scheduler.GetResourceToken(results))],
                    dependency: range.Ready);
            }
        }
        private readonly struct Read(EventReader<PhysicsEvent> reader, EventReadRange range,
            List<(long Number, PhysicsEvent Event)> results) : IJob
        {
            public void Execute()
            {
                using (var values = reader.Open(in range))
                    foreach (PhysicsEvent value in values) results.Add((value.StepNumber, value));
                reader.Advance(range.Through);
            }
        }
    }

    private static Entity CreateBody(World world, Vector3 position)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState()); world.Add(body, new AvbdBodyEquation());
        return body;
    }

    private static ContactManifolds<AvbdContactState> CreatePoint(Scheduler scheduler, World world,
        Entity floor, Entity body, Vector3 bodyAnchor, float separation)
    {
        Vector3 delta = world.ReadComponentData<BodyPose>(body).Position + bodyAnchor - world.ReadComponentData<BodyPose>(floor).Position;
        world.Add(body, new ReportContacts());
        return ContactTestData.Create(scheduler, floor, body,
            new ContactPoint { Normal = Vector3.UnitY, Separation = separation, BodyAnchorDelta = delta, FirstBodyCount = 1 },
            [new(floor, Vector3.Zero, 1), new(body, bodyAnchor, 1)],
            new ContactMaterial { NormalStiffness = float.PositiveInfinity }, new AvbdContactState { NormalPenalty = 1e6f });
    }
}
