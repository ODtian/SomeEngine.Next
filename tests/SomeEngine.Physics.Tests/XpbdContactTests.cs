using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class XpbdContactTests
{
    [Fact]
    public void XpbdContactSupportsTheSphereAndWritesItsPhysicalForce()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        Entity point = CreateContact(world, floor, sphere, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 1e6f }, [new(floor, Vector3.Zero, 1)], [new(sphere, -Vector3.UnitY, 1)]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>());
        QueryHandle contacts = ReadContacts(world);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveXpbd.Create(world, bodies, new XpbdConstraints<ContactPoint, XpbdContactProjection>(contacts), 8))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, .99998f, 1.00001f);
        Assert.InRange(world.Read<BodyVelocity>(sphere).Linear.Y, -.001f, .001f);
        Assert.InRange(world.Read<ContactResponse>(point).ForceOnSecond.Y, 9.99f, 10.01f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void InterleavedContactAndLengthIterationsKeepTheSharedNodePredictionAndMultipliers()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        world.Add(plane, new PlaneGeometry(Vector3.UnitX, 1.5f));
        world.Add(plane, new ContactMaterial { NormalStiffness = 1000 });
        Entity firstAttachment = CreateBody(world, new BodyPose { Position = -Vector3.UnitZ, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Static });
        Entity secondAttachment = CreateBody(world, new BodyPose { Position = Vector3.UnitZ, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Static });
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f);
        Entity first = CreateBody(world, new BodyPose { Position = new Vector3(2, 0, -1), Rotation = rotation },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity second = CreateBody(world, new BodyPose { Position = new Vector3(2, 0, 1), Rotation = rotation },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        foreach (Entity body in new[] { plane, firstAttachment, secondAttachment, first, second })
            world.Add(body, new AvbdBodyEquation());
        Entity edge = world.CreateEntity(new DynamicEdge(new(first, Vector3.Zero), new(second, Vector3.Zero)));
        world.Add(edge, new ContactMaterial { NormalStiffness = 1000 });
        world.CreateRelation(firstAttachment, first, new XpbdLength { RestLength = 0, Compliance = 1 });
        world.CreateRelation(secondAttachment, second, new XpbdLength { RestLength = 0, Compliance = 1 });
        QueryHandle planes = world.Query(new QueryDefinitionBuilder().All<PlaneGeometry>());
        QueryHandle edges = world.Query(new QueryDefinitionBuilder().All<DynamicEdge>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle pointScope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        var pairs = world.Between(planes, edges).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        using var detected = new ContactPointBuffer(scheduler, 4, 12);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<PlaneGeometry, CapsuleGeometry>.Detect(
            new ComponentGeometry<PlaneGeometry>(), new DynamicEdgeReader(),
            CollisionGeometry<CapsuleGeometry, PlaneGeometry>.Reverse(new ConvexPlaneCollision<CapsuleGeometry>(1e-5f)),
            new ContactPointOutput(detected), contactDistance: 1));
        using var detection = DetectContactPoints.Create(world, pairs, bins, new ContactTypes(), narrow, detected,
            ReadOnlySpan<JobResourceAccess>.Empty);
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1000 }), new ContactRefreshSettings(.02f, .99f, false));
        var avbdConstraints = AvbdConstraintGroups.Combine(new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts),
            new XpbdForces<XpbdLength, XpbdLengthProjection>(lengths));
        var avbd = SolveAvbd.Create(world, bodies, avbdConstraints, 1, 0, 0);
        var xpbd = SolveXpbd.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths), 1);
        var stages = new ApplyGyroscopicTorque(64).Then(new PredictBodies(Vector3.Zero, 64))
            .Then(detection).Then(points.Refresh()).Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(Alternate(avbd.Iterate(1), xpbd.Iterate(1), 24)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        Assert.Equal(2, world.GetIncidentRelations<ContactPoint>(edge).Count);
        Assert.InRange(Vector3.Distance(new Vector3(1.499002f, 0, -1), world.Read<BodyPose>(first).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(1.499002f, 0, 1), world.Read<BodyPose>(second).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-.500998f, 0, 0), world.Read<BodyVelocity>(first).Linear), 0, 1e-4f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(rotation, world.Read<BodyPose>(first).Rotation)), .999999f, 1.000001f);
        Assert.InRange(world.Read<BodyVelocity>(first).Angular.Length(), 0, 1e-6f);
        foreach (var contact in world.GetIncidentRelations<ContactPoint>(edge).Entries)
            Assert.InRange(world.Read<ContactResponse>(contact.Edge.Entity).ForceOnSecond.X, .997f, .999f);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(edges);
        world.ReleaseQuery(planes);
    }

    [Theory]
    [InlineData(.03f, .04f, 0, 0, .5f)]
    [InlineData(3, 4, 2.880012f, 3.840016f, 1.9998f)]
    public void XpbdFrictionUsesTheCombinedTangentMagnitude(float x, float z, float finalX, float finalZ, float tangentForce)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        world.Replace(body, new BodyVelocity { Linear = new Vector3(x, 0, z) });
        Entity point = CreateContact(world, floor, body, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 1e6f, StaticFriction = .4f, DynamicFriction = .2f },
            [new(floor, Vector3.Zero, 1)], [new(body, Vector3.Zero, 1)]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>());
        QueryHandle contacts = ReadContacts(world);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveXpbd.Create(world, bodies, new XpbdConstraints<ContactPoint, XpbdContactProjection>(contacts), 8))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        BodyVelocity velocity = world.Read<BodyVelocity>(body);
        Assert.InRange(Vector3.Distance(new Vector3(finalX, 0, finalZ), new Vector3(velocity.Linear.X, 0, velocity.Linear.Z)), 0, 1e-4f);
        Vector3 force = world.Read<ContactResponse>(point).ForceOnSecond;
        Assert.InRange(MathF.Abs(new Vector2(force.X, force.Z).Length() - tangentForce), 0, 2e-4f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdApproachDampingUsesTheCommonStepStart()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        world.Replace(body, new BodyVelocity { Linear = -Vector3.UnitY });
        Entity point = CreateContact(world, floor, body, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 100, NormalDamping = 10 },
            [new(floor, Vector3.Zero, 1)], [new(body, Vector3.Zero, 1)]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>());
        QueryHandle contacts = ReadContacts(world);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveXpbd.Create(world, bodies, new XpbdConstraints<ContactPoint, XpbdContactProjection>(contacts), 4))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.Y, .96666f, .96667f);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Y, -.33334f, -.33332f);
        Assert.InRange(world.Read<ContactResponse>(point).ForceOnSecond.Y, 6.666f, 6.667f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void GraphContactRecoveryUsesCurrentLengthBendAndUserForces()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity first = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity middle = CreateBody(world, new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity last = CreateBody(world, new BodyPose { Position = new Vector3(1, 1, 0), Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        foreach (Entity body in new[] { floor, first, middle, last }) world.Add(body, new AvbdBodyEquation());
        Entity point = CreateContact(world, floor, first, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 10000 }, [new(floor, Vector3.Zero, 1)], [new(first, Vector3.Zero, 1)]);
        world.Add(point, new AvbdContactState { NormalPenalty = 10000 });
        world.CreateRelation(first, middle, new XpbdLength { RestLength = 1, Compliance = .001f });
        world.CreateRelation(middle, last, new XpbdLength { RestLength = 1, Compliance = .001f });
        world.CreateEntity(new XpbdBend { First = first, Middle = middle, Last = last, MinimumCosine = .5f, Compliance = .001f });
        Entity height = world.CreateEntity(new HeightTarget { Body = middle, Height = .1f });
        world.Add(height, new HeightMultiplier());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle bends = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdBend>());
        QueryHandle heights = world.Query(new QueryDefinitionBuilder().Read<HeightTarget>().ReadWrite<HeightMultiplier>());
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Colors, 0, 8, 1, 2, false);
        var forces = AvbdGraphConstraints.Combine(AvbdGraphConstraints.Combine(
            new XpbdForces<XpbdLength, XpbdLengthProjection>(lengths), new XpbdForces<XpbdBend, XpbdBendProjection>(bends)),
            new XpbdForces<HeightTarget, HeightProjection>(heights));
        using var graphAvbd = SolveAvbdGraph.Create(world, bodies,
            AvbdGraphConstraints.Combine(new AvbdGraphConstraints<ContactPoint, AvbdContactMath>(contacts), forces), settings, 1, 0, 0);
        using var graphXpbd = SolveXpbdGraph.Create(world, bodies, XpbdGraphConstraints.Combine(
            XpbdGraphConstraints.Combine(new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
                new XpbdGraphConstraints<XpbdBend, XpbdBendProjection>(bends)),
            new XpbdGraphConstraints<HeightTarget, HeightProjection>(heights)), settings, 1);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(graphAvbd.Initialize()).Then(graphXpbd.Initialize())
            .Then(Alternate(graphAvbd.Iterate(1), graphXpbd.Iterate(1), 64)).Then(new UpdateBodyVelocities(64));
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();
    


        Vector3 a = world.Read<BodyPose>(first).Position;
        Vector3 b = world.Read<BodyPose>(middle).Position;
        Vector3 c = world.Read<BodyPose>(last).Position;
        Assert.InRange(a.Y, -.001f, .001f);
        Assert.InRange(Vector3.Distance(a, b), .999f, 1.001f);
        Assert.InRange(Vector3.Distance(b, c), .999f, 1.001f);
        Assert.InRange(Vector3.Dot(Vector3.Normalize(b - a), Vector3.Normalize(c - b)), .495f, .505f);
        Assert.InRange(b.Y, .09999f, .10001f);
        Assert.True(world.Read<ContactResponse>(point).ForceOnSecond.Y > .01f);
        world.ReleaseQuery(heights);
        world.ReleaseQuery(bends);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void BuiltInAndUserForcesFollowTheChangingNodeDirectionsDuringContactRecovery()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity first = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity middle = CreateBody(world, new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity last = CreateBody(world, new BodyPose { Position = new Vector3(1, 1, 0), Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        foreach (Entity body in new[] { floor, first, middle, last }) world.Add(body, new AvbdBodyEquation());
        Entity point = CreateContact(world, floor, first, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 10000 }, [new(floor, Vector3.Zero, 1)], [new(first, Vector3.Zero, 1)]);
        world.Add(point, new AvbdContactState { NormalPenalty = 10000 });
        world.CreateRelation(first, middle, new XpbdLength { RestLength = 1, Compliance = .001f });
        world.CreateRelation(middle, last, new XpbdLength { RestLength = 1, Compliance = .001f });
        world.CreateEntity(new XpbdBend { First = first, Middle = middle, Last = last, MinimumCosine = .5f, Compliance = .001f });
        Entity height = world.CreateEntity(new HeightTarget { Body = middle, Height = .1f });
        world.Add(height, new HeightMultiplier());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle bends = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdBend>());
        QueryHandle heights = world.Query(new QueryDefinitionBuilder().Read<HeightTarget>().ReadWrite<HeightMultiplier>());
        var internalForces = AvbdConstraintGroups.Combine(
            AvbdConstraintGroups.Combine(new XpbdForces<XpbdLength, XpbdLengthProjection>(lengths), new XpbdForces<XpbdBend, XpbdBendProjection>(bends)),
            new XpbdForces<HeightTarget, HeightProjection>(heights));
        var avbd = SolveAvbd.Create(world, bodies,
            AvbdConstraintGroups.Combine(new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), internalForces), 1, 0, 0);
        var xpbd = SolveXpbd.Create(world, bodies, new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths)
            .And(new XpbdConstraints<XpbdBend, XpbdBendProjection>(bends)).And(new XpbdConstraints<HeightTarget, HeightProjection>(heights)), 1);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(avbd.Initialize()).Then(xpbd.Initialize()).Then(Alternate(avbd.Iterate(1), xpbd.Iterate(1), 64))
            .Then(new UpdateBodyVelocities(64));
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();
    


        Vector3 a = world.Read<BodyPose>(first).Position;
        Vector3 b = world.Read<BodyPose>(middle).Position;
        Vector3 c = world.Read<BodyPose>(last).Position;
        Assert.InRange(a.Y, -.001f, .001f);
        Assert.InRange(Vector3.Distance(a, b), .999f, 1.001f);
        Assert.InRange(Vector3.Distance(b, c), .999f, 1.001f);
        Assert.InRange(Vector3.Dot(Vector3.Normalize(b - a), Vector3.Normalize(c - b)), .495f, .505f);
        Assert.InRange(b.Y, .09999f, .10001f);
        Assert.True(world.Read<ContactResponse>(point).ForceOnSecond.Y > .01f);
        world.ReleaseQuery(heights);
        world.ReleaseQuery(bends);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void PersistentXpbdContactsFeedTheCommonRestitutionAfterVelocityReconstruction()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        world.Add(floor, new PlaneGeometry(Vector3.UnitY, 0));
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(sphere, new SphereGeometry(1));
        world.Replace(sphere, new BodyVelocity { Linear = new Vector3(0, -2, 0) });
        var material = new ContactMaterial { NormalStiffness = 1e6f, Restitution = .5f };
        world.Add(floor, material);
        world.Add(sphere, material);
        QueryHandle floors = world.Query(new QueryDefinitionBuilder().All<PlaneGeometry>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<BodyVelocity>());
        QueryHandle contacts = ReadContacts(world);
        QueryHandle pointScope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        var pairs = world.Between(floors, spheres).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<PlaneGeometry, SphereGeometry>.Detect(
            CollisionGeometry<SphereGeometry, PlaneGeometry>.Reverse(new ConvexPlaneCollision<SphereGeometry>(1e-5f)),
            new ContactPointOutput(detected), 0));
        using var detection = DetectContactPoints.Create(world, pairs, bins, new ContactTypes(), narrow, detected,
            ReadOnlySpan<JobResourceAccess>.Empty);
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new XpbdContactComponents(), new ContactRefreshSettings(.02f, .99f, false));
        var stages = new PredictBodies(Vector3.Zero, 64).Then(detection).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveXpbd.Create(world, bodies, new XpbdConstraints<ContactPoint, XpbdContactProjection>(contacts), 8))
            .Then(new UpdateBodyVelocities(64)).Then(new ApplyContactRestitution(world, bodies, contacts, 1));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.Equal(1, world.GetIncidentRelations<ContactPoint>(sphere).Count);
        Assert.InRange(world.Read<BodyPose>(sphere).Position.Y, .99997f, 1.00001f);
        Assert.InRange(world.Read<BodyVelocity>(sphere).Linear.Y, .9999f, 1.0001f);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(spheres);
        world.ReleaseQuery(floors);
    }

    [Fact]
    public void XpbdMultiEndContactCombinesTheSameBodyAcrossBothSides()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity first = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        Entity second = CreateBody(world, new BodyPose { Position = new Vector3(1, 1, 0), Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 3 });
        Entity point = CreateContact(world, floor, second, new ContactPoint { Normal = Vector3.UnitY },
            new ContactMaterial { NormalStiffness = 1e6f }, [new(floor, Vector3.Zero, .75f), new(first, Vector3.Zero, .25f)],
            [new(first, Vector3.Zero, .75f), new(second, Vector3.Zero, .25f)]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>());
        QueryHandle contacts = ReadContacts(world);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts))
            .Then(SolveXpbd.Create(world, bodies, new XpbdConstraints<ContactPoint, XpbdContactProjection>(contacts), 4))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(first).Position.Y, .98569f, .98570f);
        Assert.InRange(world.Read<BodyPose>(second).Position.Y, 1.02853f, 1.02855f);
        Assert.InRange(world.Read<ContactResponse>(point).ForceOnSecond.Y, 17.138f, 17.140f);
        Assert.InRange(world.Read<BodyVelocity>(first).Angular.Length(), 0, 1e-6f);
        world.ReleaseQuery(contacts);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdJointForcesSupplyTranslationAndTorqueToAvbd()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, new BodyMass { Motion = BodyMotion.Static });
        Entity target = CreateBody(world, new BodyPose { Position = new Vector3(2, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f) },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(source, new AvbdBodyEquation());
        world.Add(target, new AvbdBodyEquation());
        var joint = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
            LinearX = JointAxisLimit.Locked, AngularZ = JointAxisLimit.Locked,
            LinearStiffness = Vector3.UnitX, AngularStiffness = Vector3.UnitZ,
        });
        world.Add(joint.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var xpbd = SolveXpbd.Create(world, bodies, new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints), 1);
        var avbd = SolveAvbd.Create(world, bodies, new XpbdForces<JointConstraint, XpbdJointProjection>(joints), 1, 0, 0);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(xpbd.Initialize()).Then(avbd.Initialize())
            .Then(Alternate(avbd.Iterate(1), xpbd.Iterate(1), 24)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.Read<BodyPose>(target).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(.9553365f, .2955202f, 0),
            Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(target).Rotation)), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(target).Linear.X, -1.0001f, -.9999f);
        Assert.InRange(world.Read<BodyVelocity>(target).Angular.Z, -.3001f, -.2999f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorForcesKeepTheSelectedReactionAndForceTorqueLimitsInAvbd()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var mass = new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity };
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, mass);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, mass);
        world.Add(source, new AvbdBodyEquation());
        world.Add(target, new AvbdBodyEquation());
        var joint = world.CreateRelation(source, target, new JointConstraint { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(joint.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Both,
            LinearStiffness = Vector3.UnitX, MaximumForce = new Vector3(.4f, 0, 0),
            AngularStiffness = Vector3.UnitZ, MaximumTorque = new Vector3(0, 0, .1f),
        });
        world.Add(joint.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(new Vector3(2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f)),
        });
        world.Add(joint.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var xpbd = SolveXpbd.Create(world, bodies, new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors), 1);
        var avbd = SolveAvbd.Create(world, bodies, new XpbdForces<JointConstraint, XpbdJointMotorProjection>(motors), 1, 0, 0);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(xpbd.Initialize()).Then(avbd.Initialize())
            .Then(Alternate(avbd.Iterate(1), xpbd.Iterate(1), 24)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-.4f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(.4f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-4f);
        Assert.InRange(world.Read<BodyVelocity>(source).Angular.Z, -.1001f, -.0999f);
        Assert.InRange(world.Read<BodyVelocity>(target).Angular.Z, .0999f, .1001f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    private readonly struct ContactTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    private static AlternatingIterations<TFirst, TSecond> Alternate<TFirst, TSecond>(TFirst first, TSecond second, int rounds)
        where TFirst : struct, IPhysicsStage where TSecond : struct, IPhysicsStage => new(first, second, rounds);

    private readonly struct AlternatingIterations<TFirst, TSecond>(TFirst first, TSecond second, int rounds) : IPhysicsStage
        where TFirst : struct, IPhysicsStage where TSecond : struct, IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            for (int round = 0; round < rounds; round++)
                dependency = second.Schedule(in context, first.Schedule(in context, dependency));
            return dependency;
        }
    }

    private static QueryHandle ReadContacts(World world) => world.Query(new QueryDefinitionBuilder()
        .Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
        .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
        .ReadWrite<XpbdContactState>().ReadWrite<ContactResponse>());

    private static Entity CreateBody(World world, BodyPose pose, BodyMass mass)
    {
        Entity body = world.CreateEntity(pose);
        world.Add(body, mass);
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        return body;
    }

    private static Entity CreateContact(World world, Entity first, Entity second, ContactPoint point,
        ContactMaterial material, ContactBody[] firstBodies, ContactBody[] secondBodies)
    {
        point.FirstBodyCount = firstBodies.Length;
        Vector3 delta = default;
        foreach (ContactBody anchor in firstBodies)
        {
            BodyPose pose = world.Read<BodyPose>(anchor.Body);
            delta -= (pose.Position + Vector3.Transform(anchor.LocalPoint, pose.Rotation)) * anchor.Weight;
        }
        foreach (ContactBody anchor in secondBodies)
        {
            BodyPose pose = world.Read<BodyPose>(anchor.Body);
            delta += (pose.Position + Vector3.Transform(anchor.LocalPoint, pose.Rotation)) * anchor.Weight;
        }
        point.BodyAnchorDelta = delta;
        Entity contact = world.CreateRelation(first, second, point).Entity;
        world.Add(contact, material);
        world.Add(contact, new XpbdContactState());
        world.Add(contact, new ContactResponse());
        world.AddBuffer<ContactBodyLink>(contact);
        foreach (ContactBody anchor in firstBodies.Concat(secondBodies))
        {
            var participant = world.CreateRelation(contact, anchor.Body, new ContactParticipant());
            world.ExecuteBufferWrite<ContactBodyLink>(contact,
                buffer => buffer.Add(new(participant, anchor.LocalPoint, anchor.Weight)));
        }
        return contact;
    }
}
