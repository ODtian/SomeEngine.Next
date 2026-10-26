using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class CompletedPhysicsEventsTests
{
    private struct CollisionEvents;

    [Fact]
    public void AnXpbdConnectionBreaksUsingForceUnitsFromItsCurrentSubstepMultiplier()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitX);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity connection = world.CreateRelation(anchor, body, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(connection, new ConstraintBreakSettings(3, float.PositiveInfinity));
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<ConstraintBreakSettings>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var output = new PhysicsEventBuffer(scheduler, 4);
        using var breaking = new BreakConstraints<XpbdLength, XpbdLengthProjection>(world, bodies, lengths, output);
        var solve = SolveXpbd.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths), 1);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordEvents>(world, events, new(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new PredictBodies(Vector3.UnitX * 4, 64).Then(solve).Then(new UpdateBodyVelocities(64)).Then(breaking).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .25, 0, 0, 0);

        frame.AdvanceTime(.25); frame.CompleteForRead();

        var item = Assert.Single(received);
        Assert.Equal(connection, item.Event.Subject); Assert.Equal(anchor, item.Event.Body);
        Assert.Equal(new Vector3(4, 0, 0), item.Event.Force);
        Assert.Equal(Vector3.UnitX, world.Read<BodyPose>(body).Position);
        frame.AdvanceTime(.25); frame.CompleteForRead();
        Assert.Equal(new Vector3(1.25f, 0, 0), world.Read<BodyPose>(body).Position);
        world.ReleaseQuery(events); world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }


    [Fact]
    public void AJointBreakingAtItsBodyForceLimitReleasesTheNextStepAndDeliversItsLoad()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitX);
        world.Replace(anchor, new BodyMass { Motion = BodyMotion.Static });
        Entity joint = world.CreateRelation(anchor, body, JointConstraints.CreateFixed(JointFrame.Identity,
            new JointFrame(-Vector3.UnitX, Quaternion.Identity), float.PositiveInfinity, float.PositiveInfinity)).Entity;
        world.Add(joint, new AvbdJointState { LinearPenalty = new(1e6f), AngularPenalty = new(1e6f) });
        world.Add(joint, new ConstraintBreakSettings(4, float.PositiveInfinity));
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .ReadWrite<AvbdJointState>().Read<ConstraintBreakSettings>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var output = new PhysicsEventBuffer(scheduler, 4);
        using var breaking = new BreakConstraints<JointConstraint, AvbdJointMath>(world, bodies, joints, output);
        var solve = SolveAvbd.Create(world, bodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(joints), 48, 0, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordEvents>(world, events, new(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new PredictBodies(new Vector3(0, -8, 0), 64).Then(solve).Then(new UpdateBodyVelocities(64)).Then(breaking).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);

        frame.AdvanceTime(.125); frame.CompleteForRead();

        Assert.Equal(0, world.GetOutgoingRelations<JointConstraint>(anchor).Count);
        var item = Assert.Single(received);
        Assert.Equal(PhysicsEventKind.Break, item.Event.Kind); Assert.Equal(joint, item.Event.Subject); Assert.Equal(0, item.Number);
        Assert.Equal(anchor, item.Event.First); Assert.Equal(body, item.Event.Second);
        Assert.Equal(anchor, item.Event.Body);
        Assert.InRange(MathF.Abs(item.Event.Force.Y), 7.9f, 8.1f);
        float supportedHeight = world.Read<BodyPose>(body).Position.Y;
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.InRange(world.Read<BodyPose>(body).Position.Y - supportedHeight, -.1251f, -.1249f);
        world.ReleaseQuery(events); world.ReleaseQuery(joints); world.ReleaseQuery(bodies);
    }


    [Fact]
    public void ATriggerReportsEntryAndExitWhileTheBodyPassesThroughItsPlane()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitY * .125f);
        world.Replace(floor, new BodyMass { Motion = BodyMotion.Static });
        world.Replace(body, new BodyVelocity { Linear = -Vector3.UnitY * 2 });
        Entity point = CreatePoint(world, floor, body, Vector3.Zero, .125f);
        world.Add(point, new TriggerContact()); world.Add(point, new ContactEventState<CollisionEvents>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var output = new PhysicsEventBuffer(scheduler, 4);
        using var observation = new ContactEvents<CollisionEvents>(world, bodies, contacts, output, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordEvents>(world, events, new(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new PredictBodies(Vector3.Zero, 64).Then(new UpdateBodyVelocities(64)).Then(observation).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.Equal(-.125f, world.Read<BodyPose>(body).Position.Y);
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitY * 2 });

        frame.AdvanceTime(.125); frame.CompleteForRead();

        Assert.Equal(.125f, world.Read<BodyPose>(body).Position.Y);
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.All(received, item => { Assert.Equal(PhysicsEventKind.Trigger, item.Event.Kind); Assert.Equal(Vector3.Zero, item.Event.Force); });
        Assert.Equal(-.125f, received[0].Event.Separation);
        Assert.Equal(.125f, received[1].Event.Separation);
        world.ReleaseQuery(events); world.ReleaseQuery(contacts); world.ReleaseQuery(bodies);
    }


    [Fact]
    public void ContactEventsFollowTheSolvedPointAndItsGeometryDestruction()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, Vector3.Zero), body = CreateBody(world, Vector3.UnitY);
        world.Replace(floor, new BodyMass { Motion = BodyMotion.Static });
        Entity point = CreatePoint(world, floor, body, -Vector3.UnitY, 0);
        world.Add(point, new ContactEventState<CollisionEvents>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Read<UndirectedRelationEndpoints<ContactPoint>>()
            .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var output = new PhysicsEventBuffer(scheduler, 8);
        using var observation = new ContactEvents<CollisionEvents>(world, bodies, contacts, output, .001f);
        var solve = SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 0, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordEvents>(world, events, new(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new PredictBodies(new Vector3(0, -8, 0), 64).Then(new InitializeContactResponses(world, bodies, contacts)).Then(solve)
                .Then(new UpdateBodyVelocities(64)).Then(observation).Then(output.Emit()), 1);
        using var frame = physics.CreateFrame(consumer, .125, 0, 0, 0);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        Assert.InRange(world.Read<BodyPose>(body).Position.Y, .9999f, 1.0001f);
        frame.AdvanceTime(.125); frame.CompleteForRead();
        world.DestroyEntity(floor);

        frame.AdvanceTime(.125); frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, received.Select(item => item.Number));
        Assert.All(received, item => { Assert.Equal(PhysicsEventKind.Contact, item.Event.Kind); Assert.Equal(point, item.Event.Subject);
            Assert.Equal(floor, item.Event.First); Assert.Equal(body, item.Event.Second); });
        Assert.InRange(received[0].Event.Force.Y, 7.98f, 8.02f);
        Assert.InRange(world.Read<BodyPose>(body).Position.Y, .8749f, .8751f);
        Assert.False(world.Has<ContactEventState<CollisionEvents>>(point));
        world.ReleaseQuery(events); world.ReleaseQuery(contacts); world.ReleaseQuery(bodies);
    }

    [Fact]
    public void TheFrameConsumerReceivesAnIntermediateStepsSleepEventBeforeAcknowledgingIt()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity body = CreateBody(world, Vector3.Zero);
        world.Add(body, new BodyActivity { Radius = 1 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var output = new PhysicsEventBuffer(scheduler, 4);
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        using var activity = solve.Activity(new BodyActivitySettings(.05f, .1f, .01f, .01f), output);
        using var prediction = new PredictActiveBodies(world, bodies, Vector3.Zero);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordEvents>(world, events, new(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
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
        Assert.True(world.Read<BodyActivity>(body).Sleeping);
        world.ExecuteReadQuery(events, static rows => { int count = 0; foreach (QueryRow row in rows.Rows) count++; Assert.Equal(0, count); });
        double wakeTime = result.EndTime;
        world.AddBuffer<BodyWake>(body);
        world.ExecuteBufferWrite<BodyWake>(body, requests => requests.Add(new BodyWake(wakeTime)));
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.Equal(3, received.Count);
        Assert.Equal((3L, PhysicsEventKind.Wake), (received[1].Number, received[1].Event.Kind));
        Assert.Equal(wakeTime, received[1].Event.Time);
        Assert.Equal(0, received[1].Event.Substep);
        Assert.Equal((3L, PhysicsEventKind.Sleep), (received[2].Number, received[2].Event.Kind));
        world.ReleaseQuery(events); world.ReleaseQuery(lengths); world.ReleaseQuery(bodies);
    }

    private readonly struct RecordEvents(List<(long Number, PhysicsEvent Event)> results) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        {
            foreach (PhysicsEvent value in events) results.Add((step.Number, value));
        }
    }

    private static Entity CreateBody(World world, Vector3 position)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState()); world.Add(body, new AvbdBodyEquation());
        return body;
    }

    private static Entity CreatePoint(World world, Entity floor, Entity body, Vector3 bodyAnchor, float separation)
    {
        Vector3 delta = world.Read<BodyPose>(body).Position + bodyAnchor - world.Read<BodyPose>(floor).Position;
        Entity point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = separation, BodyAnchorDelta = delta, FirstBodyCount = 1,
        }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links => { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, bodyAnchor, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(point, new ContactResponse()); world.Add(point, new AvbdContactState { NormalPenalty = 1e6f });
        return point;
    }
}
