using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class XpbdConstraintTests
{
    [Fact]
    public void LengthProjectionSharesMotionByInverseMassAndUpdatesVelocity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity first = CreateBody(world, Vector3.Zero, 1);
        Entity second = CreateBody(world, new Vector3(3, 0, 0), 3);
        Relations<XpbdLength>.CreateEdge(world, first, second, new XpbdLength { RestLength = 1 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 1))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(new Vector3(.5f, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(1.5f, 0, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(.5f, 0, 0), world.ReadComponentData<BodyVelocity>(first).Linear);
        Assert.Equal(new Vector3(-1.5f, 0, 0), world.ReadComponentData<BodyVelocity>(second).Linear);
    }

    [Fact]
    public void BendProjectionMovesItsThreeEntityEndpoints()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity first = CreateBody(world, Vector3.Zero, 1);
        Entity middle = CreateBody(world, Vector3.UnitX, 1);
        Entity last = CreateBody(world, new Vector3(1, 1, 0), 1);
        ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f }, [first, middle, last]);
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query bends = world.Query(ConstraintBodies.AddReads(new QueryDescription().WithStored<XpbdBend>()));
        var constraints = new XpbdConstraints<XpbdBend, XpbdBendProjection>(bends);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 1))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(new Vector3(0, -.125f, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(.875f, .125f, 0), world.ReadComponentData<BodyPose>(middle).Position);
        Assert.Equal(new Vector3(1.125f, 1, 0), world.ReadComponentData<BodyPose>(last).Position);
    }

    [Fact]
    public void UserProjectionSharesIterationsWithBuiltInLength()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity attachment = CreateBody(world, Vector3.Zero, 0);
        Entity point = CreateBody(world, new Vector3(3, 2, 0), 1);
        Relations<XpbdLength>.CreateEdge(world, attachment, point, new XpbdLength { RestLength = MathF.Sqrt(2) });
        Entity target = world.Create(new HeightTarget { Body = point, Height = 1 });
        world.Add(target, new HeightMultiplier());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query heights = world.Query(new QueryDescription().WithStored<HeightTarget>().WithStored<HeightMultiplier>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths)
            .And(new XpbdConstraints<HeightTarget, HeightProjection>(heights));
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 16))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(1, 1, 0), world.ReadComponentData<BodyPose>(point).Position), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(attachment).Position);
    }

    [Fact]
    public void CompliantLengthUsesTheSubstepDurationAndAccumulatesWithinEachSolve()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity attachment = CreateBody(world, Vector3.Zero, 0);
        Entity point = CreateBody(world, new Vector3(2, 0, 0), 1);
        Relations<XpbdLength>.CreateEdge(world, attachment, point, new XpbdLength
        {
            RestLength = 1,
            Compliance = 1,
        });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 4))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 2);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(1.48f, world.ReadComponentData<BodyPose>(point).Position.X, 5);
        Assert.Equal(-.64f, world.ReadComponentData<BodyVelocity>(point).Linear.X, 5);
    }

    private static Entity CreateBody(World world, Vector3 position, float inverseMass)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass
        {
            InverseMass = inverseMass,
            InverseInertia = Matrix4x4.Identity,
            Motion = inverseMass == 0 ? BodyMotion.Static : BodyMotion.Dynamic,
        });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        return body;
    }
}

public struct HeightTarget : IComponent
{
    public Entity Body;
    public float Height;
}

public struct HeightMultiplier : IComponent
{
    public float Value;
}

public readonly struct HeightProjection : IXpbdConstraint<HeightTarget>, IXpbdConstraintForces<HeightTarget>
{
    private readonly ConstraintColumns<HeightTarget, HeightMultiplier> _fields;
    public HeightProjection() => _fields = new();
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses) =>
        accesses.AddRange(ConstraintColumns<HeightTarget, HeightMultiplier>.Accesses(world, members));
    public void Bind(World world, Query members) => _fields.Bind(world, members);
    public ref readonly HeightTarget ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static int GetBodyCount(in HeightTarget constraint, World world, Entity entity, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in HeightTarget constraint, World world, Entity entity, int index, in PhysicsStepContext context) => constraint.Body;
    public static void GetBodyForce(in HeightTarget constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        float duration = (float)context.Duration;
        force = Vector3.UnitY * (world.ReadComponentData<HeightMultiplier>(entity).Value / (duration * duration));
        torque = Vector3.Zero;
    }
    public void Initialize(in HeightTarget constraint, World world, Entity entity, in PhysicsStepContext context) => _fields.WriteState(entity).Value = 0;
    public void Project(in HeightTarget constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref BodyPose pose = ref bodies.WritePose(constraint.Body);
        float inverseMass = bodies.ReadMass(constraint.Body).InverseMass;
        float change = -(pose.Position.Y - constraint.Height) / inverseMass;
        _fields.WriteState(entity).Value += change;
        pose.Position.Y += inverseMass * change;
    }
}
