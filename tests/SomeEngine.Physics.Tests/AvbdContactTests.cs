using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class AvbdContactTests
{
    [Fact]
    public void UnilateralContactSupportsTheSphereAgainstGravity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        var sphereGeometry = new SphereGeometry(1);
        var planeGeometry = new PlaneGeometry(Vector3.UnitY, 0);
        world.Add(sphere, sphereGeometry);
        world.Add(plane, planeGeometry);
        BodyPose spherePose = world.ReadComponentData<BodyPose>(sphere);
        BodyPose planePose = world.ReadComponentData<BodyPose>(plane);
        CollisionDistance geometry = new ConvexPlaneCollision<SphereGeometry>(1e-5f).Distance(
            in sphereGeometry, in spherePose, in planeGeometry, in planePose);
        var first = sphere;
        var second = plane;
        using var contacts = CreateContact(scheduler, sphere, plane, new ContactPoint
        {
            Normal = geometry.Normal,
            Separation = geometry.Separation,
            BodyAnchorDelta = geometry.SecondPoint - geometry.FirstPoint,
            FirstBodyCount = 1,
        },
            [new(first, -Vector3.UnitY, 1), new(second, Vector3.Zero, 1)],
            new ContactMaterial { NormalStiffness = 1e6f }, new AvbdContactState { NormalPenalty = 1e6f });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var constraints = new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(sphere).Position.Y, 0.99998f, 1.00001f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(sphere).Linear.Y, -0.001f, 0.001f);
    }

    [Fact]
    public void RepeatedBodyAnchorsEnterOneCombinedEquation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var fixedAnchor = floor;
        var firstAnchor = body;
        var secondAnchor = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
        },
            [new(fixedAnchor, Vector3.Zero, 1), new(firstAnchor, -Vector3.UnitX, 0.5f), new(secondAnchor, Vector3.UnitX, 0.5f)],
            new ContactMaterial { NormalStiffness = 100 }, new AvbdContactState { NormalPenalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 1, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y, 0.947367f, 0.947370f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(body).Angular.Length(), 0, 1e-5f);
        Assert.Equal(1, ContactTestData.Read(scheduler, contacts).BodyPairs[body].Length);
    }

    [Fact]
    public void DiskFrictionSlowsDiagonalSlidingByItsCombinedMagnitude()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(3, 0, 4) });
        var first = floor;
        var second = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
        },
            [new(first, Vector3.Zero, 1), new(second, Vector3.Zero, 1)],
            new ContactMaterial { NormalStiffness = 1e6f, StaticFriction = 0.3f, DynamicFriction = 0.2f }, new AvbdContactState
            {
                NormalPenalty = 1e6f,
                TangentPenalty = 100,
                FrictionRegularizationSpeed = 0.01f,
            });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(2.88f, 0, 3.84f), world.ReadComponentData<BodyVelocity>(body).Linear), 0, 1e-3f);
    }

    [Fact]
    public void StaticFrictionHoldsWhenTheRequiredForceFitsItsHigherBound()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(0.2f, 0, 0.2f) });
        var first = floor;
        var second = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
        },
            [new(first, Vector3.Zero, 1), new(second, Vector3.Zero, 1)],
            new ContactMaterial { NormalStiffness = 1e6f, StaticFriction = 0.3f, DynamicFriction = 0.2f }, new AvbdContactState
            {
                NormalPenalty = 1e6f,
                TangentPenalty = 100,
                FrictionRegularizationSpeed = 0.01f,
            });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyVelocity>(body).Linear.Length(), 0, 1e-3f);
    }

    [Fact]
    public void ApproachDampingUsesTheMaterialCoefficient()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = -Vector3.UnitY });
        var first = floor;
        var second = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
        },
            [new(first, Vector3.Zero, 1), new(second, Vector3.Zero, 1)],
            new ContactMaterial { NormalStiffness = 100, NormalDamping = 10 }, new AvbdContactState { NormalPenalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y, 0.96666f, 0.96668f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(body).Linear.Y, -0.3334f, -0.3332f);
    }

    [Fact]
    public void RestitutionUsesTheCombinedMaterialAndTheApproachVelocity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new Vector3(0, -2, 0) });
        world.Add(floor, new ContactMaterial { NormalStiffness = 1e6f, Restitution = 0.2f, RestitutionThreshold = 0.1f });
        world.Add(body, new ContactMaterial { NormalStiffness = 1e6f, Restitution = 0.8f, RestitutionThreshold = 0.1f });
        var first = floor;
        var second = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
        },
            [new(first, Vector3.Zero, 1), new(second, Vector3.Zero, 1)],
            new AverageContactMaterials().Combine(world.ReadComponentData<ContactMaterial>(floor), world.ReadComponentData<ContactMaterial>(body)), new AvbdContactState { NormalPenalty = 1e6f });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        Query velocities = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyVelocity>());
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64)).Then(new ApplyContactRestitution(world, velocities, contacts.Columns, iterations: 1));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyVelocity>(body).Linear.Y, 0.9999f, 1.0001f);

        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(body).Position.Y, 1.0998f, 1.1001f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(body).Linear.Y, 0.9999f, 1.0001f);
    }

    [Fact]
    public void WarmForcesFollowTheChangedContactFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var first = floor;
        var second = body;
        using var contacts = CreateContact(scheduler, floor, body, new ContactPoint
        {
            Normal = Vector3.UnitY,
            Separation = 0,
            BodyAnchorDelta = Vector3.UnitY,
            FirstBodyCount = 1,
            WarmStart = true,
        },
            [new(first, Vector3.Zero, 1), new(second, Vector3.Zero, 1)],
            new ContactMaterial { NormalStiffness = 1e6f }, new AvbdContactState { NormalPenalty = 100 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var constraints = new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts);
        var support = new PredictBodies(new Vector3(0, -10, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 1, 0)).Then(new UpdateBodyVelocities(64));
        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), support, 1).Schedule(0, 0, 0.1).Complete();
        BodyPose before = world.ReadComponentData<BodyPose>(body);
        world.Replace(body, new BodyVelocity());
        ContactPoint changed = ContactTestData.Read(scheduler, contacts).Points[0];
        changed.Normal = new Vector3(0.6f, 0.8f, 0);
        changed.BodyAnchorDelta = before.Position;
        ContactTestData.SetPoint(scheduler, contacts, 0, in changed);
        var turned = new PredictBodies(new Vector3(-6, -8, 0), 64)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, constraints, 1, 1, 0)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), turned, 1).Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.006f, -0.008f, 0), world.ReadComponentData<BodyPose>(body).Position - before.Position), 0, 1e-4f);
    }

    [Fact]
    public void PersistentContactsRetainTheirStateAndEndOnSeparation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        world.Add(sphere, new SphereGeometry(1));
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(sphere, new ContactMaterial { NormalStiffness = 1e6f });
        world.Add(plane, new ContactMaterial { NormalStiffness = 1e6f });
        Query spheres = world.Query(new QueryDescription().With<SphereGeometry>());
        Query planes = world.Query(new QueryDescription().With<PlaneGeometry>());
        Query poses = world.Query(new QueryDescription().WithStored<BodyPose>());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        var pairs = world.Between(spheres, planes).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        using var detected = new ContactPointBuffer(scheduler, contactCapacity: 4, bodyCapacity: 8);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
            new ConvexPlaneCollision<SphereGeometry>(1e-5f), new ContactPointOutput(detected), contactDistance: 0));
        var detection = DetectContactPoints.Create(world, pairs, bins, new SpherePlaneTypes(), narrow, detected,
            ReadOnlySpan<JobResourceAccess>.Empty);
        using var contacts = new ContactManifolds<AvbdContactState>(scheduler, new AvbdContactState
        { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
        var refresh = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies,
            contacts, detected, new(), new(.02f, .99f, true));
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64).Then(detection).Then(refresh)
            .Then(new InitializeContactResponses(world, bodies, contacts.Columns))
            .Then(SolveAvbd.Create(world, bodies, new AvbdContactConstraints<AvbdContactState, AvbdContactMath>(contacts), 32, 1, 0))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();
        Assert.Single(ContactTestData.Read(scheduler, contacts).Pairs);
        Assert.True(ContactTestData.Read(scheduler, contacts).States[0].NormalMultiplier < 0);

        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.Equal(1, contacts.Columns.PointCount);
        Assert.True(ContactTestData.Read(scheduler, contacts).Points[0].WarmStart);
        Assert.InRange(world.ReadComponentData<BodyPose>(sphere).Position.Y, 0.9998f, 1.0001f);
        BodyPose sphereBefore = world.ReadComponentData<BodyPose>(sphere), planeBefore = world.ReadComponentData<BodyPose>(plane);
        sphereBefore.Position.X += .1f; planeBefore.Position.X += .1f;
        world.Replace(sphere, sphereBefore); world.Replace(plane, planeBefore);
        physics.Schedule(2, .2, .1).Complete();
        Assert.Equal(1, contacts.Columns.PointCount); Assert.True(ContactTestData.Read(scheduler, contacts).Points[0].WarmStart);
        Assert.InRange(ContactTestData.Read(scheduler, contacts).Responses[0].ForceOnSecond.Length(), 9.9f, 10.1f);
        Assert.InRange(world.ReadComponentData<BodyPose>(sphere).Position.X, .0999f, .1001f);
        world.Replace(sphere, new BodyVelocity { Linear = new Vector3(0, 3, 0) });

        physics.Schedule(3, .3, .1).Complete();

        Assert.Equal(0, contacts.Columns.PointCount);
        Assert.Empty(ContactTestData.Read(scheduler, contacts).Pairs);
        Assert.InRange(world.ReadComponentData<BodyPose>(sphere).Position.Y, 1.199f, 1.201f);
    }

    [Fact]
    public void RefreshReleasesOnlyTheSelectedContactGroup()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity first = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity second = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        world.Add(first, new ContactMaterial { NormalStiffness = 100 }); world.Add(second, new ContactMaterial { NormalStiffness = 100 });
        Query bodies = world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>());
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        new ContactPointOutput(detected).Add(new CollisionContact { First = first, Second = second, Normal = Vector3.UnitY },
            [new(first, Vector3.Zero, 1)], [new(second, Vector3.Zero, 1)]);
        using var inner = new ContactManifolds<AvbdContactState>(scheduler, default);
        using var outer = new ContactManifolds<AvbdContactState>(scheduler, default);
        var refreshInner = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies, inner, detected, new(), new(.01f, .99f, true));
        var refreshOuter = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies, outer, detected, new(), new(.01f, .99f, true));
        PhysicsSimulation.Create(world, bodies, refreshInner.Then(refreshOuter), 1).Schedule(0, 0, .1).Complete();
        Assert.Equal(1, inner.Columns.PointCount); Assert.Equal(1, outer.Columns.PointCount);
        detected.Clear();

        PhysicsSimulation.Create(world, bodies, refreshInner, 1).Schedule(1, .1, .1).Complete();

        Assert.Equal(0, inner.Columns.PointCount); Assert.Equal(1, outer.Columns.PointCount);
        Assert.Equal(second, ContactTestData.Read(scheduler, outer).Links[0][1].Body);
    }

    [Fact]
    public void ReplacingADestroyedParticipantStartsANewContactState()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity floor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity body = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity replacement = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity floorGeometry = world.Create(new PlaneGeometry(Vector3.UnitY, 0));
        Entity sphereGeometry = world.Create(new SphereGeometry(1));
        world.Add(floorGeometry, new ContactMaterial { NormalStiffness = 100 }); world.Add(sphereGeometry, new ContactMaterial { NormalStiffness = 100 });
        Query bodies = world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>());
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        var output = new ContactPointOutput(detected);
        var contact = new CollisionContact { First = floorGeometry, Second = sphereGeometry, Normal = Vector3.UnitY };
        output.Add(in contact, [new(floor, Vector3.Zero, 1)], [new(body, Vector3.Zero, 1)]);
        using var contacts = new ContactManifolds<AvbdContactState>(scheduler, new AvbdContactState { NormalPenalty = 100 });
        var refresh = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies, contacts, detected, new(), new(.01f, .99f, true));
        var physics = PhysicsSimulation.Create(world, bodies, refresh, 1);
        physics.Schedule(0, 0, .1).Complete(); AvbdContactState previousState = ContactTestData.Read(scheduler, contacts).States[0];
        previousState.NormalMultiplier = -7; ContactTestData.SetState(scheduler, contacts, 0, in previousState);
        world.Destroy(body); detected.Clear();
        output.Add(in contact, [new(floor, Vector3.Zero, 1)], [new(replacement, Vector3.Zero, 1)]);

        physics.Schedule(1, .1, .1).Complete();

        Assert.Equal(1, contacts.Columns.PointCount); Assert.Equal(replacement, ContactTestData.Read(scheduler, contacts).Links[0][1].Body);
        Assert.Equal(0, ContactTestData.Read(scheduler, contacts).States[0].NormalMultiplier); Assert.False(ContactTestData.Read(scheduler, contacts).Points[0].WarmStart);
    }

    [Fact]
    public void ContactModificationFailurePropagatesThroughTheStepCompletion()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity sphere = CreateBody(world, new BodyPose { Position = Vector3.UnitY, Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity plane = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Query bodies = world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>());
        using var detected = new ContactPointBuffer(scheduler, 1, 2);
        new ContactPointOutput(detected).Add(new CollisionContact { First = plane, Second = sphere, Normal = Vector3.UnitY },
            [new(plane, Vector3.Zero, 1)], [new(sphere, Vector3.Zero, 1)]);
        using var contacts = new ContactManifolds<AvbdContactState>(scheduler, default);
        var refresh = new RefreshContactManifolds<AvbdContactState, AverageContactMaterials>(world, bodies, contacts, detected, new(), new(.1f, .99f, true));
        var error = new InvalidOperationException("The contact modification failed.");
        var physics = PhysicsSimulation.Create(world, bodies, refresh.Then(new FailingModification(contacts.Columns, error)), 1);

        PhysicsStepResult result = physics.Schedule(0, 0, .1);

        AggregateException failure = Assert.Throws<AggregateException>(() => result.Complete());
        Assert.Same(error, Assert.Single(failure.Flatten().InnerExceptions));
        Assert.Equal(1, contacts.Columns.PointCount);
        world.Create(new BodyStepState());
    }

    private readonly record struct FailingModification(ContactColumns Contacts, InvalidOperationException Error) : IPhysicsStage, IJob
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            (context.World.Execution!.Scheduler!).Schedule(this, [Contacts.WriteAccess], dependency: dependency);
        public void Execute() => throw Error;
    }

    private readonly struct SpherePlaneTypes : ICollisionPairClassifier
    {
        public QueryDescription AddReads(QueryDescription definition) => definition;
        public CollisionPairType Classify(World world, Entity first, Entity second) => new(0, 0, 1);
    }

    private static Entity CreateBody(World world, BodyPose pose, BodyMotion motion)
    {
        Entity body = world.Create(pose);
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
    private static ContactManifolds<AvbdContactState> CreateContact(Scheduler scheduler, Entity first, Entity second,
        ContactPoint point, ReadOnlySpan<ContactBodyLink> links, ContactMaterial material, AvbdContactState state) =>
        ContactTestData.Create(scheduler, first, second, point, links, material, state);
}
