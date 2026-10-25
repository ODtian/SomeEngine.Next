using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PredictiveCollisionTests
{
    [Fact]
    public void PredictedBoundsAndStartContactsStopTheSphereCrossingAThinWall()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sphere = CreateBody(world, new Vector3(-3, 0, 0), BodyMotion.Dynamic);
        Entity wall = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        world.Add(sphere, new SphereGeometry(.5f));
        world.Add(wall, new BoxGeometry(new Vector3(.1f, 2, 2)));
        world.Replace(sphere, new BodyVelocity { Linear = new Vector3(60, 0, 0) });
        world.Add(sphere, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(wall, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        QueryHandle geometryMembers = world.Query(new QueryDefinitionBuilder().Read<CollisionBounds>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var candidates = Collision.Candidates<CollisionBounds, CollisionBoundsReader>(world, geometryMembers, new CollisionTree(), default);
        using var maintenance = candidates.CreateMaintenance(scheduler);
        using var bins = new CollisionPairBins<CollisionNodePair>(scheduler, 8, 1);
        using var detected = new ContactPointBuffer(scheduler, 4, 8);
        var kernel = CollisionGeometry<SphereGeometry, BoxGeometry>.Predict(new ComponentGeometryMotion<SphereGeometry>(),
            new ComponentGeometryMotion<BoxGeometry>(), new SphereCollision<BoxGeometry>(), new ContactPointOutput(detected), contactDistance: 0);
        using var detection = DetectContactPoints.Create(world, maintenance.Query, bins, new SphereBoxTypes(),
            StepContactNarrowPhase.Create(0, kernel), detected, [maintenance.ReadAccess]);
        using var points = ContactPoints.Create(world, poses, scope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1e6f }), new ContactRefreshSettings(.02f, .99f, false));
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(new UpdatePredictedBounds<SphereGeometry>(0, 64)).Then(new UpdatePredictedBounds<BoxGeometry>(0, 64))
            .Then(new RefitBounds(maintenance)).Then(detection).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 8, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        physics.Schedule(17, 10, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(sphere).Position.X, -.6001f, -.5999f);
        var point = world.GetIncidentRelations<ContactPoint>(sphere).Entries[0].Edge;
        Assert.True(world.Read<ContactPoint>(point.Entity).Prediction.Enabled);
        Assert.Equal(10, world.Read<ContactPoint>(point.Entity).Prediction.SampleTime);
        Assert.Equal(10, world.Read<ContactResponse>(point.Entity).StartTime);
        Assert.Equal(10.1, world.Read<ContactResponse>(point.Entity).EndTime);

        physics.Schedule(18, 10.1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(sphere).Position.X, -.6001f, -.5999f);
        Assert.InRange(world.Read<BodyVelocity>(sphere).Linear.Length(), 0, 1e-3f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(scope);
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(geometryMembers);
    }

    [Fact]
    public void UserMotionGeometrySamplesEachSubstepAndActsOnItsReferencedBody()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity body = CreateBody(world, new Vector3(-3, 0, 0), BodyMotion.Dynamic);
        Entity geometry = world.CreateEntity(new ReferencedSphere(body, .5f));
        world.Add(geometry, new CollisionBounds());
        Entity wall = CreateBody(world, Vector3.Zero, BodyMotion.Static);
        world.Add(wall, new BoxGeometry(new Vector3(.1f, 2, 2)));
        world.Replace(body, new BodyVelocity { Linear = new Vector3(60, 0, 0) });
        world.Add(geometry, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(wall, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        QueryHandle geometryMembers = world.Query(new QueryDefinitionBuilder().Read<CollisionBounds>().Any<ReferencedSphere>().Any<BoxGeometry>());
        QueryHandle referenced = world.Query(new QueryDefinitionBuilder().All<ReferencedSphere>());
        var motion = new ReferencedSphereMotion();
        QueryHandle access = world.Query(motion.AddReads(QueryDefinition.Empty));
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var candidates = Collision.Candidates<CollisionBounds, CollisionBoundsReader>(world, geometryMembers, new CollisionTree(), default);
        using var maintenance = candidates.CreateMaintenance(scheduler);
        using var update = new UpdatePredictedBounds<SphereGeometry, ReferencedSphereMotion>(world, referenced, access, motion, padding: 0);
        using var bins = new CollisionPairBins<CollisionNodePair>(scheduler, 8, 1);
        using var detected = new ContactPointBuffer(scheduler, 4, 8);
        var kernel = CollisionGeometry<SphereGeometry, BoxGeometry>.Predict(motion, new ComponentGeometryMotion<BoxGeometry>(),
            new SphereCollision<BoxGeometry>(), new ContactPointOutput(detected), contactDistance: 0);
        using var detection = DetectContactPoints.Create(world, maintenance.Query, bins, new SphereBoxTypes(),
            StepContactNarrowPhase.Create(0, kernel), detected, [maintenance.ReadAccess]);
        using var points = ContactPoints.Create(world, poses, scope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1e6f }), new ContactRefreshSettings(.02f, .99f, false));
        var stages = new PredictBodies(Vector3.Zero, 64).Then(update).Then(new UpdatePredictedBounds<BoxGeometry>(0, 64))
            .Then(new RefitBounds(maintenance)).Then(detection).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 8, 0, 0))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2).Schedule(5, 2, .2).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.X, -.6001f, -.5999f);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, 1e-3f);
        Assert.Equal(1, world.GetIncomingRelations<ContactParticipant>(body).Count);
        var point = world.GetIncidentRelations<ContactPoint>(geometry).Entries[0].Edge;
        Assert.Equal(2.1, world.Read<ContactPoint>(point.Entity).Prediction.SampleTime);
        Assert.Equal(2.1, world.Read<ContactResponse>(point.Entity).StartTime);
        Assert.Equal(2.2, world.Read<ContactResponse>(point.Entity).EndTime);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(scope);
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(access);
        world.ReleaseQuery(referenced);
        world.ReleaseQuery(geometryMembers);
    }

    private readonly record struct ReferencedSphere(Entity Body, float Radius) : IComponent;
    private readonly struct ReferencedSphereMotion : ICollisionGeometryMotion<SphereGeometry>
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<ReferencedSphere>()
            .WithOptionalRead<BodyPose>().WithOptionalRead<BodyMass>().WithOptionalRead<BodyStepState>();
        public SphereGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
        {
            ReferencedSphere reference = row.Read<ReferencedSphere>();
            access.TryGetRow(reference.Body, out QueryRow body);
            pose = body.Read<BodyPose>(); return new(reference.Radius);
        }
        public SphereGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
        {
            ReferencedSphere reference = row.Read<ReferencedSphere>();
            access.TryGetRow(reference.Body, out QueryRow body);
            ref readonly BodyStepState step = ref body.ReadRef<BodyStepState>();
            pose = new BodyPose { Rotation = step.StartRotation };
            pose.SetCenterOfMass(step.StartCenterOfMass, in body.ReadRef<BodyMass>());
            return new(reference.Radius);
        }
        public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 1;
        public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
        {
            _ = ReadGeometry(row, access, out BodyPose pose);
            bodies[0] = new(row.Read<ReferencedSphere>().Body, Vector3.Transform(point - pose.Position, Quaternion.Conjugate(pose.Rotation)), 1);
        }
        public void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
        {
            _ = ReadStartGeometry(row, access, out BodyPose pose);
            bodies[0] = new(row.Read<ReferencedSphere>().Body, Vector3.Transform(point - pose.Position, Quaternion.Conjugate(pose.Rotation)), 1);
        }
        public CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding)
        {
            SphereGeometry sphere = ReadStartGeometry(row, access, out BodyPose start);
            access.TryGetRow(row.Read<ReferencedSphere>().Body, out QueryRow body);
            ref readonly BodyStepState step = ref body.ReadRef<BodyStepState>();
            var end = new BodyPose { Rotation = step.PredictedRotation };
            end.SetCenterOfMass(step.PredictedCenterOfMass, in body.ReadRef<BodyMass>());
            CollisionBounds first = SphereGeometry.Bounds(in sphere, in start), last = SphereGeometry.Bounds(in sphere, in end);
            CollisionBounds bounds = CollisionBounds.Union(in first, in last);
            return new(bounds.Min - new Vector3(padding), bounds.Max + new Vector3(padding));
        }
    }

    private readonly struct RefitBounds(CollisionMaintenance<CollisionBounds, CollisionBoundsReader, SpatialAll, GroupMatch> maintenance) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => maintenance.Schedule(dependency);
    }
    private readonly struct SphereBoxTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 1);
    }
    private static Entity CreateBody(World world, Vector3 position, BodyMotion motion)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        world.Add(body, new CollisionBounds());
        return body;
    }
}
