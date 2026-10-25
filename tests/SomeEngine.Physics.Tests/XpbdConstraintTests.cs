using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class XpbdConstraintTests
{
    [Fact]
    public void LengthProjectionSharesMotionByInverseMassAndUpdatesVelocity()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, Vector3.Zero, 1);
        Entity second = CreateBody(world, new Vector3(3, 0, 0), 3);
        world.CreateEntity(new XpbdLength { First = first, Second = second, RestLength = 1 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 1))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(new Vector3(.5f, 0, 0), world.Read<BodyPose>(first).Position);
        Assert.Equal(new Vector3(1.5f, 0, 0), world.Read<BodyPose>(second).Position);
        Assert.Equal(new Vector3(.5f, 0, 0), world.Read<BodyVelocity>(first).Linear);
        Assert.Equal(new Vector3(-1.5f, 0, 0), world.Read<BodyVelocity>(second).Linear);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void BendProjectionMovesItsThreeEntityEndpoints()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, Vector3.Zero, 1);
        Entity middle = CreateBody(world, Vector3.UnitX, 1);
        Entity last = CreateBody(world, new Vector3(1, 1, 0), 1);
        world.CreateEntity(new XpbdBend { First = first, Middle = middle, Last = last, MinimumCosine = .5f });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle bends = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdBend>());
        var constraints = new XpbdConstraints<XpbdBend, XpbdBendProjection>(bends);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 1))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(new Vector3(0, -.125f, 0), world.Read<BodyPose>(first).Position);
        Assert.Equal(new Vector3(.875f, .125f, 0), world.Read<BodyPose>(middle).Position);
        Assert.Equal(new Vector3(1.125f, 1, 0), world.Read<BodyPose>(last).Position);
        world.ReleaseQuery(bends);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void UserProjectionSharesIterationsWithBuiltInLength()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity attachment = CreateBody(world, Vector3.Zero, 0);
        Entity point = CreateBody(world, new Vector3(3, 2, 0), 1);
        world.CreateEntity(new XpbdLength { First = attachment, Second = point, RestLength = MathF.Sqrt(2) });
        Entity target = world.CreateEntity(new HeightTarget { Body = point, Height = 1 });
        world.Add(target, new HeightMultiplier());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>());
        QueryHandle heights = world.Query(new QueryDefinitionBuilder().Read<HeightTarget>().ReadWrite<HeightMultiplier>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths)
            .And(new XpbdConstraints<HeightTarget, HeightProjection>(heights));
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 16))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(1, 1, 0), world.Read<BodyPose>(point).Position), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(attachment).Position);
        world.ReleaseQuery(heights);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void CompliantLengthUsesTheSubstepDurationAndAccumulatesWithinEachSolve()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity attachment = CreateBody(world, Vector3.Zero, 0);
        Entity point = CreateBody(world, new Vector3(2, 0, 0), 1);
        world.CreateEntity(new XpbdLength
        {
            First = attachment, Second = point, RestLength = 1, Compliance = 1,
        });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().ReadWrite<XpbdLength>());
        var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, iterations: 4))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(1.48f, world.Read<BodyPose>(point).Position.X, 5);
        Assert.Equal(-.64f, world.Read<BodyVelocity>(point).Linear.X, 5);
        world.ReleaseQuery(lengths);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position, float inverseMass)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
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

public readonly struct HeightProjection : IXpbdConstraint<HeightTarget>
{
    public static void Initialize(in HeightTarget constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<HeightMultiplier>().Value = 0;

    public static void Project(in HeightTarget constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref BodyPose pose = ref bodies.WritePose(constraint.Body);
        float inverseMass = bodies.ReadMass(constraint.Body).InverseMass;
        float change = -(pose.Position.Y - constraint.Height) / inverseMass;
        row.ReadWrite<HeightMultiplier>().Value += change;
        pose.Position.Y += inverseMass * change;
    }
}
