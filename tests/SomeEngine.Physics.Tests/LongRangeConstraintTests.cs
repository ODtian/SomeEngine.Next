using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class LongRangeConstraintTests
{
    [Fact]
    public void SelectedJointBoundsReduceTheAnchoredChainEndTravelInOneRound()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reference = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity previous = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        Entity last = default;
        for (int index = 0; index < 6; index++)
        {
            last = CreateBody(world, new Vector3(0, -2 * index - 1, 0), BodyMotion.Dynamic);
            var joint = world.CreateRelation(previous, last, JointConstraints.CreateBall(
                new JointFrame(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
            world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(100) });
            world.AddBuffer<LongRangeJointBound>(joint.Entity);
            previous = last;
        }
        Entity referencePrevious = CreateBody(reference, Vector3.Zero, BodyMotion.Static);
        Entity referenceLast = default;
        for (int index = 0; index < 6; index++)
        {
            referenceLast = CreateBody(reference, new Vector3(0, -2 * index - 1, 0), BodyMotion.Dynamic);
            var joint = reference.CreateRelation(referencePrevious, referenceLast, JointConstraints.CreateBall(
                new JointFrame(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
            reference.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(100) });
            referencePrevious = referenceLast;
        }
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        QueryHandle longRangeJoints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Buffer<LongRangeJointBound>(QueryAccess.ReadWrite));
        var constraints = AvbdConstraintGroups.Combine(new AvbdConstraints<JointConstraint, AvbdJointMath>(joints),
            new JointLongRangeConstraints(longRangeJoints, new JointLongRangeSettings(4, 32, .99f, .99f)));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 0, 0)).Then(new UpdateBodyVelocities(64));
        QueryHandle referenceBodies = reference.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle referenceJoints = reference.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var referenceStages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(SolveAvbd.Create(reference, referenceBodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(referenceJoints), 1, 0, 0))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, referenceStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        float referenceTravel = MathF.Abs(reference.Read<BodyPose>(referenceLast).Position.Y + 11);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(last).Position.Y + 11), 0, referenceTravel * .5f);
        reference.ReleaseQuery(referenceJoints);
        reference.ReleaseQuery(referenceBodies);
        world.ReleaseQuery(longRangeJoints);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void SelectedContactSupportsKeepTheStackLevelWhileOtherBodiesFall()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        Entity lower = CreateBody(world, Vector3.UnitY, BodyMotion.Dynamic);
        Entity upper = CreateBody(world, Vector3.UnitY * 3, BodyMotion.Dynamic);
        Entity falling = CreateBody(world, new Vector3(5, 10, 0), BodyMotion.Dynamic);
        foreach (Entity body in new[] { lower, upper })
        {
            world.Add(body, new LongRangeBodySupport());
            world.AddBuffer<LongRangeSupportLoad>(body);
            world.AddBuffer<LongRangeSupportEquation>(body);
        }
        foreach (Entity body in new[] { lower, upper })
        {
            BodyMass mass = world.Read<BodyMass>(body);
            mass.CenterOfMass = new Vector3(.25f, 0, .2f);
            world.Replace(body, mass);
        }
        for (int layer = 0; layer < 2; layer++)
            foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
            {
                Entity firstBody = layer == 0 ? floor : lower, secondBody = layer == 0 ? lower : upper;
                var point = world.CreateRelation(firstBody, secondBody, new ContactPoint
                {
                    Normal = Vector3.UnitY, FirstBodyCount = 1, WorldPoint = new(corner.X, layer * 2, corner.Y),
                });
                var first = world.CreateRelation(point.Entity, firstBody, new ContactParticipant());
                var second = world.CreateRelation(point.Entity, secondBody, new ContactParticipant());
                world.AddBuffer<ContactBodyLink>(point.Entity);
                world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
                {
                    links.Add(new(first, new(corner.X, layer == 0 ? 0 : 1, corner.Y), 1));
                    links.Add(new(second, new(corner.X, -1, corner.Y), 1));
                });
                world.Add(point.Entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = 1, DynamicFriction = .5f });
                world.Add(point.Entity, new ContactResponse());
                world.Add(point.Entity, new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
                world.Add(point.Entity, new LongRangeContactSupport());
                world.AddBuffer<LongRangeSupportLoad>(point.Entity);
            }
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle supportBodies = world.Query(new QueryDefinitionBuilder().ReadWrite<LongRangeBodySupport>()
            .Buffer<LongRangeSupportLoad>(QueryAccess.ReadWrite).Buffer<LongRangeSupportEquation>(QueryAccess.ReadWrite));
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>().ReadWrite<LongRangeContactSupport>()
            .Buffer<LongRangeSupportLoad>(QueryAccess.ReadWrite));
        var constraints = AvbdConstraintGroups.Combine(new AvbdConstraints<ContactPoint, AvbdSupportedContactMath>(contacts),
            new ContactLongRangeConstraints(supportBodies, contacts, new ContactSupportSettings(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f)));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 0, 0)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(MathF.Abs(world.Read<BodyPose>(lower).Position.Y - 1), 0, 1e-4f);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(upper).Position.Y - 3), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(lower).Angular.Length(), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(upper).Angular.Length(), 0, 1e-4f);
        float pressure = 0;
        foreach (var entry in world.GetIncidentRelations<ContactPoint>(floor).Entries)
            pressure += world.Read<ContactResponse>(entry.Edge.Entity).ForceOnSecond.Y;
        Assert.InRange(pressure, 19.99f, 20.01f);
        Assert.InRange(world.Read<BodyPose>(falling).Position.Y, 9.8999f, 9.9001f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(supportBodies);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void FiniteJointMaterialsKeepTheirExtensionWhenLongRangeWorkIsSelected()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var reference = WorldJobs.CreateWorld(scheduler: scheduler);
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        World[] worlds = [reference, world];
        var tips = new Entity[2];
        var bodyQueries = new QueryHandle[2];
        var jointQueries = new QueryHandle[2];
        var rangeQueries = new QueryHandle[2];
        for (int group = 0; group < worlds.Length; group++)
        {
            World current = worlds[group];
            Entity previous = CreateBody(current, Vector3.Zero, BodyMotion.Static);
            for (int index = 0; index < 2; index++)
            {
                Entity body = CreateBody(current, new Vector3(0, -2 * index - 1, 0), BodyMotion.Dynamic);
                var joint = current.CreateRelation(previous, body, JointConstraints.CreateBall(
                    new JointFrame(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                    new JointFrame(Vector3.UnitY, Quaternion.Identity), linearStiffness: 100));
                current.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(100) });
                current.AddBuffer<LongRangeJointBound>(joint.Entity);
                previous = body;
            }
            tips[group] = previous;
            bodyQueries[group] = current.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
                .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
            jointQueries[group] = current.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
                .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
            rangeQueries[group] = current.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
                .Read<DirectedRelationEndpoints<JointConstraint>>().Buffer<LongRangeJointBound>(QueryAccess.ReadWrite));
        }
        var referenceConstraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(jointQueries[0]);
        var constraints = AvbdConstraintGroups.Combine(new AvbdConstraints<JointConstraint, AvbdJointMath>(jointQueries[1]),
            new JointLongRangeConstraints(rangeQueries[1], new JointLongRangeSettings(4, 32, .99f, .99f)));
        var referenceStages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(SolveAvbd.Create(reference, bodyQueries[0], referenceConstraints, 1, 0, 0)).Then(new UpdateBodyVelocities(64));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(SolveAvbd.Create(world, bodyQueries[1], constraints, 1, 0, 0)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, referenceStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(Vector3.Distance(reference.Read<BodyPose>(tips[0]).Position, world.Read<BodyPose>(tips[1]).Position), 0, 1e-6f);
        for (int group = 0; group < worlds.Length; group++)
        {
            worlds[group].ReleaseQuery(rangeQueries[group]);
            worlds[group].ReleaseQuery(jointQueries[group]);
            worlds[group].ReleaseQuery(bodyQueries[group]);
        }
    }

    [Fact]
    public void UpdatedJointFramesChangeTheLongRangeReachInTheNextStep()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity root = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        Entity first = CreateBody(world, -Vector3.UnitY, BodyMotion.Dynamic);
        Entity last = CreateBody(world, -Vector3.UnitY * 3, BodyMotion.Dynamic);
        var rootJoint = world.CreateRelation(root, first, JointConstraints.CreateBall(JointFrame.Identity,
            new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
        var nextJoint = world.CreateRelation(first, last, JointConstraints.CreateBall(
            new JointFrame(-Vector3.UnitY, Quaternion.Identity), new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
        foreach (Entity joint in new[] { rootJoint.Entity, nextJoint.Entity })
        {
            world.Add(joint, new AvbdJointState { LinearPenalty = new Vector3(100) });
            world.AddBuffer<LongRangeJointBound>(joint);
        }
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        QueryHandle rangeJoints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Buffer<LongRangeJointBound>(QueryAccess.ReadWrite));
        var constraints = AvbdConstraintGroups.Combine(new AvbdConstraints<JointConstraint, AvbdJointMath>(joints),
            new JointLongRangeConstraints(rangeJoints, new JointLongRangeSettings(4, 32, .99f, .99f)));
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .1).Complete();
        JointConstraint changed = world.Read<JointConstraint>(rootJoint.Entity);
        changed.TargetFrame = new JointFrame(Vector3.UnitY * 2, Quaternion.Identity);
        world.Replace(rootJoint.Entity, changed);
        world.Replace(first, new BodyPose { Position = -Vector3.UnitY * 2, Rotation = Quaternion.Identity });
        world.Replace(last, new BodyPose { Position = -Vector3.UnitY * 4, Rotation = Quaternion.Identity });

        physics.Schedule(1, .1, .1).Complete();

        Assert.InRange(Vector3.Distance(-Vector3.UnitY * 4, world.Read<BodyPose>(last).Position), 0, 1e-5f);
        world.ReleaseQuery(rangeJoints);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void SlidingReleasesTheHeightBoundsAndReturnsTheirReactionToTheContact()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        Entity body = CreateBody(world, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(body, new LongRangeBodySupport());
        world.AddBuffer<LongRangeSupportLoad>(body);
        world.AddBuffer<LongRangeSupportEquation>(body);
        foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
        {
            var point = world.CreateRelation(floor, body, new ContactPoint
            {
                Normal = Vector3.UnitY, FirstBodyCount = 1, WorldPoint = new(corner.X, 0, corner.Y), WarmStart = true,
            });
            var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
            var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
            world.AddBuffer<ContactBodyLink>(point.Entity);
            world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
            {
                links.Add(new(first, new(corner.X, 0, corner.Y), 1));
                links.Add(new(second, new(corner.X, -1, corner.Y), 1));
            });
            world.Add(point.Entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
            world.Add(point.Entity, new ContactResponse());
            world.Add(point.Entity, new AvbdContactState { NormalPenalty = 100 });
            world.Add(point.Entity, new LongRangeContactSupport());
            world.AddBuffer<LongRangeSupportLoad>(point.Entity);
        }
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle supportBodies = world.Query(new QueryDefinitionBuilder().ReadWrite<LongRangeBodySupport>()
            .Buffer<LongRangeSupportLoad>(QueryAccess.ReadWrite).Buffer<LongRangeSupportEquation>(QueryAccess.ReadWrite));
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>().ReadWrite<LongRangeContactSupport>()
            .Buffer<LongRangeSupportLoad>(QueryAccess.ReadWrite));
        var constraints = AvbdConstraintGroups.Combine(new AvbdConstraints<ContactPoint, AvbdSupportedContactMath>(contacts),
            new ContactLongRangeConstraints(supportBodies, contacts,
                new ContactSupportSettings(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, 1, .99f, 1e-6f)));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 1, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .1).Complete();
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitX });

        physics.Schedule(1, .1, .1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(.1f, 1, 0), world.Read<BodyPose>(body).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.Read<BodyVelocity>(body).Linear), 0, 1e-4f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(supportBodies);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position, BodyMotion motion)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
