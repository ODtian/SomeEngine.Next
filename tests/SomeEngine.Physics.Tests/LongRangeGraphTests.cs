using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class LongRangeGraphTests
{
    [Fact]
    public void EditedJointFramesAndEndpointsSetTheNextGraphSolvedReach()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity root = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        Entity first = CreateBody(world, -Vector3.UnitY, BodyMotion.Dynamic);
        Entity oldLast = CreateBody(world, -Vector3.UnitY * 3, BodyMotion.Dynamic);
        Entity newLast = CreateBody(world, -Vector3.UnitY * 4, BodyMotion.Dynamic);
        var rootJoint = world.CreateRelation(root, first, JointConstraints.CreateBall(JointFrame.Identity,
            new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
        var nextJoint = world.CreateRelation(first, oldLast, JointConstraints.CreateBall(
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
            .Read<DirectedRelationEndpoints<JointConstraint>>().WriteBuffer<LongRangeJointBound>());
        var constraints = AvbdGraphConstraints.Combine(new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(joints),
            new JointLongRangeGraphConstraints(rangeJoints, new JointLongRangeSettings(4, 32, .99f, .99f)));
        using var solve = SolveAvbdGraph.Create(world, bodies, constraints,
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 32, 16, false), 1, 0, 0);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .1).Complete();
        JointConstraint changed = world.Read<JointConstraint>(rootJoint.Entity);
        changed.TargetFrame = new JointFrame(Vector3.UnitY * 2, Quaternion.Identity);
        world.Replace(rootJoint.Entity, changed);
        world.RetargetRelationImmediate(nextJoint, first, newLast);
        world.Replace(first, new BodyPose { Position = -Vector3.UnitY * 2, Rotation = Quaternion.Identity });
        world.Replace(oldLast, new BodyVelocity { Linear = Vector3.UnitX });

        physics.Schedule(1, .1, .1).Complete();

        Assert.InRange(Vector3.Distance(-Vector3.UnitY * 4, world.Read<BodyPose>(newLast).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(.1f, -3, 0), world.Read<BodyPose>(oldLast).Position), 0, 1e-5f);
        world.ReleaseQuery(rangeJoints);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ColoredSupportsKeepTheStackLevelAndTransferItsWeightToTheFloor()
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
            .WriteBuffer<LongRangeSupportLoad>().WriteBuffer<LongRangeSupportEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>().ReadWrite<LongRangeContactSupport>()
            .WriteBuffer<LongRangeSupportLoad>());
        var constraints = AvbdGraphConstraints.Combine(new AvbdGraphConstraints<ContactPoint, AvbdSupportedContactMath>(contacts),
            new ContactLongRangeGraphConstraints(supportBodies, contacts,
                new ContactSupportSettings(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f)));
        using var solve = SolveAvbdGraph.Create(world, bodies, constraints,
            new PhysicsGraphSettings(PhysicsGraphOrder.Colors, 1, 1, 1, 16, false), 1, 0, 0);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts)).Then(solve).Then(new UpdateBodyVelocities(64));

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
    public void JointBoundsReduceTheGraphSolvedChainEndTravelInOneRound()
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
            for (int index = 0; index < 6; index++)
            {
                Entity body = CreateBody(current, new Vector3(0, -2 * index - 1, 0), BodyMotion.Dynamic);
                var joint = current.CreateRelation(previous, body, JointConstraints.CreateBall(
                    new JointFrame(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                    new JointFrame(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
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
                .Read<DirectedRelationEndpoints<JointConstraint>>().WriteBuffer<LongRangeJointBound>());
        }
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 32, 16, false);
        using var referenceSolve = SolveAvbdGraph.Create(reference, bodyQueries[0],
            new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(jointQueries[0]), settings, 1, 0, 0);
        var constraints = AvbdGraphConstraints.Combine(
            new AvbdGraphConstraints<JointConstraint, AvbdJointMath>(jointQueries[1]),
            new JointLongRangeGraphConstraints(rangeQueries[1], new JointLongRangeSettings(4, 32, .99f, .99f)));
        using var solve = SolveAvbdGraph.Create(world, bodyQueries[1], constraints, settings, 1, 0, 0);
        var referenceStages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(referenceSolve).Then(new UpdateBodyVelocities(64));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, referenceStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        float referenceTravel = MathF.Abs(reference.Read<BodyPose>(tips[0]).Position.Y + 11);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(tips[1]).Position.Y + 11), 0, referenceTravel * .5f);
        for (int group = 0; group < worlds.Length; group++)
        {
            worlds[group].ReleaseQuery(rangeQueries[group]);
            worlds[group].ReleaseQuery(jointQueries[group]);
            worlds[group].ReleaseQuery(bodyQueries[group]);
        }
    }

    private static Entity CreateBody(World world, Vector3 position, BodyMotion motion)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0,
            InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
