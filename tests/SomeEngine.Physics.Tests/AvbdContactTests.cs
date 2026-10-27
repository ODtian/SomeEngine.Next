using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class AvbdContactTests
{
    [Fact]
    public void UnilateralContactSupportsTheSphereAgainstGravity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        var sphereGeometry = new SphereGeometry(1);
        var planeGeometry = new PlaneGeometry(Vector3.UnitY, 0);
        world.Add(sphere, sphereGeometry);
        world.Add(plane, planeGeometry);
        BodyPose spherePose = world.Read<BodyPose>(sphere);
        BodyPose planePose = world.Read<BodyPose>(plane);
        CollisionDistance geometry = new ConvexPlaneCollision<SphereGeometry>(1e-5f).Distance(
            in sphereGeometry, in spherePose, in planeGeometry, in planePose);
        var point = world.CreateRelation(sphere, plane, new ContactPoint
        {
            Normal = geometry.Normal, Separation = geometry.Separation,
            BodyAnchorDelta = geometry.SecondPoint - geometry.FirstPoint, FirstBodyCount = 1,
        });
        var first = world.CreateRelation(point.Entity, sphere, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, plane, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, -Vector3.UnitY, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 1e6f });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 1e6f });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var constraints = new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, 0.99998f, 1.00001f);
        Assert.InRange(world.Read<BodyVelocity>(sphere).Linear.Y, -0.001f, 0.001f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void RepeatedBodyAnchorsEnterOneCombinedEquation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1,
        });
        var fixedAnchor = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var firstAnchor = world.CreateRelation(point.Entity, body, new ContactParticipant());
        var secondAnchor = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(fixedAnchor, Vector3.Zero, 1));
            links.Add(new(firstAnchor, -Vector3.UnitX, 0.5f));
            links.Add(new(secondAnchor, Vector3.UnitX, 0.5f));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 100 });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>()
            .Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 1, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.Y, 0.947367f, 0.947370f);
        Assert.InRange(world.Read<BodyVelocity>(body).Angular.Length(), 0, 1e-5f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void DiskFrictionSlowsDiagonalSlidingByItsCombinedMagnitude()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(3, 0, 4) });
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1,
        });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, Vector3.Zero, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 1e6f, StaticFriction = 0.3f, DynamicFriction = 0.2f });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState
        {
            NormalPenalty = 1e6f, TangentPenalty = 100, FrictionRegularizationSpeed = 0.01f,
        });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(2.88f, 0, 3.84f), world.Read<BodyVelocity>(body).Linear), 0, 1e-3f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void StaticFrictionHoldsWhenTheRequiredForceFitsItsHigherBound()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(0.2f, 0, 0.2f) });
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1,
        });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, Vector3.Zero, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 1e6f, StaticFriction = 0.3f, DynamicFriction = 0.2f });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState
        {
            NormalPenalty = 1e6f, TangentPenalty = 100, FrictionRegularizationSpeed = 0.01f,
        });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, 1e-3f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ApproachDampingUsesTheMaterialCoefficient()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = -Vector3.UnitY });
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1,
        });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, Vector3.Zero, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 100, NormalDamping = 10 });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.Y, 0.96666f, 0.96668f);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Y, -0.3334f, -0.3332f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void RestitutionUsesTheCombinedMaterialAndTheApproachVelocity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(0, -2, 0) });
        world.Add(floor, new ContactMaterial { NormalStiffness = 1e6f, Restitution = 0.2f, RestitutionThreshold = 0.1f });
        world.Add(body, new ContactMaterial { NormalStiffness = 1e6f, Restitution = 0.8f, RestitutionThreshold = 0.1f });
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1,
        });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, Vector3.Zero, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new AverageContactMaterials().Combine(world.Read<ContactMaterial>(floor), world.Read<ContactMaterial>(body)));
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 1e6f });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle velocities = world.Query(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().ReadWrite<BodyVelocity>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64)).Then(new ApplyContactRestitution(world, velocities, contacts, iterations: 1));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Y, 0.9999f, 1.0001f);

        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.Y, 1.0998f, 1.1001f);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Y, 0.9999f, 1.0001f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(velocities);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void WarmForcesFollowTheChangedContactFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var point = world.CreateRelation(floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY, Separation = 0, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true,
        });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, body, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        {
            links.Add(new(first, Vector3.Zero, 1));
            links.Add(new(second, Vector3.Zero, 1));
        });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 1e6f });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 100 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var constraints = new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts);
        var support = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 1, 0)).Then(new UpdateBodyVelocities(64));
        PhysicsSimulation.Create(world, QueryDefinition.Empty, support, 1).Schedule(0, 0, 0.1).Complete();
        BodyPose before = world.Read<BodyPose>(body);
        world.Replace(body, new BodyVelocity());
        ContactPoint changed = world.Read<ContactPoint>(point.Entity);
        changed.Normal = new Vector3(0.6f, 0.8f, 0);
        changed.BodyAnchorDelta = before.Position;
        world.Replace(point.Entity, changed);
        var turned = new PredictBodies(new Vector3(-6, -8, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 1, 0)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, turned, 1).Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.006f, -0.008f, 0), world.Read<BodyPose>(body).Position - before.Position), 0, 1e-4f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void PersistentContactsReuseTheirEntityAndEndOnSeparation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        world.Add(sphere, new SphereGeometry(1));
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(sphere, new ContactMaterial { NormalStiffness = 1e6f });
        world.Add(plane, new ContactMaterial { NormalStiffness = 1e6f });
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        QueryHandle planes = world.Query(new QueryDefinitionBuilder().All<PlaneGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var pairs = world.Between(spheres, planes).PartitionPairs();
        QueryHandle pointScope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        using var detected = new ContactPointBuffer(scheduler, contactCapacity: 4, bodyCapacity: 8);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
            new ConvexPlaneCollision<SphereGeometry>(1e-5f), new ContactPointOutput(detected), contactDistance: 0));
        using var detection = DetectContactPoints.Create(world, pairs, bins, new SpherePlaneTypes(), narrow, detected,
            ReadOnlySpan<JobResourceAccess>.Empty);
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = 0.01f }),
            new ContactRefreshSettings(MatchDistance: 0.02f, MinimumNormalDot: 0.99f, WarmStart: true));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64).Then(detection).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveAvbd.Create(world, bodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), 32, 1, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();
        RelationAdjacencySnapshot<ContactPoint> first = world.GetIncidentRelations<ContactPoint>(sphere);
        Assert.Equal(1, first.Count);
        RelationEdge<ContactPoint> point = first.Entries[0].Edge;

        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.Equal(point, world.GetIncidentRelations<ContactPoint>(sphere).Entries[0].Edge);
        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, 0.9998f, 1.0001f);
        world.Replace(sphere, new BodyVelocity { Linear = new Vector3(0, 3, 0) });

        physics.Schedule(2, 0.2, 0.1).Complete();

        Assert.False(world.IsAlive(point.Entity));
        Assert.Equal(0, world.GetIncidentRelations<ContactPoint>(sphere).Count);
        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, 1.199f, 1.201f);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(planes);
        world.ReleaseQuery(spheres);
    }

    [Fact]
    public void RefreshReleasesOnlyTheSelectedContactGroup()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity second = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Add(first, new ContactMaterial { NormalStiffness = 100 });
        world.Add(second, new ContactMaterial { NormalStiffness = 100 });
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle inner = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().All<InnerContacts>());
        QueryHandle outer = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().All<OuterContacts>());
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        new ContactPointOutput(detected).Add(new CollisionContact
        {
            First = first, Second = second, Normal = Vector3.UnitY,
        }, [new ContactBody(first, Vector3.Zero, 1)], [new ContactBody(second, Vector3.Zero, 1)]);
        using var innerPoints = ContactPoints.Create(world, poses, inner, detected, new AverageContactMaterials(),
            new GroupContactComponents<InnerContacts>(), new ContactRefreshSettings(0.01f, 0.99f, true));
        using var outerPoints = ContactPoints.Create(world, poses, outer, detected, new AverageContactMaterials(),
            new GroupContactComponents<OuterContacts>(), new ContactRefreshSettings(0.01f, 0.99f, true));
        PhysicsSimulation.Create(world, QueryDefinition.Empty, innerPoints.Refresh().Then(outerPoints.Refresh()), 1)
            .Schedule(0, 0, 0.1).Complete();
        Assert.Equal(2, world.GetIncidentRelations<ContactPoint>(first).Count);
        detected.Clear();

        PhysicsSimulation.Create(world, QueryDefinition.Empty, innerPoints.Refresh(), 1).Schedule(1, 0.1, 0.1).Complete();

        RelationAdjacencySnapshot<ContactPoint> remaining = world.GetIncidentRelations<ContactPoint>(first);
        Assert.Equal(1, remaining.Count);
        Assert.True(world.Has<OuterContacts>(remaining.Entries[0].Edge.Entity));
        world.ReleaseQuery(outer);
        world.ReleaseQuery(inner);
        world.ReleaseQuery(poses);
    }

    [Fact]
    public void DestroyedBodyEndsItsContactBeforeAReplacementParticipates()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity replacement = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity floorGeometry = world.CreateEntity(new PlaneGeometry(Vector3.UnitY, 0));
        Entity sphereGeometry = world.CreateEntity(new SphereGeometry(1));
        world.Add(floorGeometry, new ContactMaterial { NormalStiffness = 100 });
        world.Add(sphereGeometry, new ContactMaterial { NormalStiffness = 100 });
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        var output = new ContactPointOutput(detected);
        var contact = new CollisionContact { First = floorGeometry, Second = sphereGeometry, Normal = Vector3.UnitY };
        output.Add(in contact, [new ContactBody(floor, Vector3.Zero, 1)], [new ContactBody(body, Vector3.Zero, 1)]);
        using var points = ContactPoints.Create(world, poses, scope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 100 }), new ContactRefreshSettings(0.01f, 0.99f, true));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, points.Refresh(), 1);
        physics.Schedule(0, 0, 0.1).Complete();
        RelationEdge<ContactPoint> previous = world.GetIncidentRelations<ContactPoint>(sphereGeometry).Entries[0].Edge;
        world.DestroyEntity(body);
        detected.Clear();
        output.Add(in contact, [new ContactBody(floor, Vector3.Zero, 1)], [new ContactBody(replacement, Vector3.Zero, 1)]);

        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.False(world.IsAlive(previous.Entity));
        Assert.Equal(1, world.GetIncidentRelations<ContactPoint>(sphereGeometry).Count);
        Assert.Equal(1, world.GetIncomingRelations<ContactParticipant>(replacement).Count);
        world.ReleaseQuery(scope);
        world.ReleaseQuery(poses);
    }

    [Fact]
    public void RefreshReportsTheComponentFailureAfterReleasingItsCommands()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        world.Add(sphere, new SphereGeometry(1));
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(sphere, new ContactMaterial { NormalStiffness = 100 });
        world.Add(plane, new ContactMaterial { NormalStiffness = 100 });
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        QueryHandle planes = world.Query(new QueryDefinitionBuilder().All<PlaneGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle pointScope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        var pairs = world.Between(spheres, planes).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        using var detected = new ContactPointBuffer(scheduler, contactCapacity: 4, bodyCapacity: 8);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
            new ConvexPlaneCollision<SphereGeometry>(1e-5f), new ContactPointOutput(detected), contactDistance: 0));
        using var detection = DetectContactPoints.Create(world, pairs, bins, new SpherePlaneTypes(), narrow, detected,
            ReadOnlySpan<JobResourceAccess>.Empty);
        var error = new InvalidOperationException("The selected contact component failed.");
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new FailingContactComponents(error), new ContactRefreshSettings(.1f, .99f, true));
        using var events = new PhysicsEventBuffer(scheduler, 1);
        QueryHandle eventRows = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var consumption = new ConsumePhysicsEvents<AcknowledgeContactEvents>(world, eventRows, default);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            detection.Then(points.Refresh()).Then(events.Emit()).Then(consumption), substeps: 1);

        PhysicsStepResult result = physics.Schedule(0, 0, .1);

        AggregateException failure = Assert.Throws<AggregateException>(() => result.Complete());
        Assert.Same(error, Assert.Single(failure.Flatten().InnerExceptions));
        world.CreateEntity(new BodyStepState());
        world.ReleaseQuery(eventRows);
        world.ReleaseQuery(pointScope); world.ReleaseQuery(poses);
        world.ReleaseQuery(planes); world.ReleaseQuery(spheres);
    }

    private readonly struct FailingContactComponents(InvalidOperationException error) : IContactPointComponents
    {
        public void AddReads(World world, List<JobResourceAccess> accesses) { }
        public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
            ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context) => throw error;
    }

    private readonly struct AcknowledgeContactEvents : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands) { }
    }

    private readonly struct InnerContacts : IComponent;
    private readonly struct OuterContacts : IComponent;
    private readonly struct GroupContactComponents<TGroup> : IContactPointComponents where TGroup : struct, IComponent
    {
        public void AddReads(World world, List<JobResourceAccess> accesses) { }
        public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
            ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context) =>
            commands.Add(point, new TGroup());
    }

    private readonly struct SpherePlaneTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 1);
    }

    private static Entity CreateBody(World world, BodyPose pose, BodyMotion motion)
    {
        Entity body = world.CreateEntity(pose);
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
