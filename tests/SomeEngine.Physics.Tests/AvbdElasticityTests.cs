using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class AvbdElasticityTests
{
    [Theory]
    [InlineData(100, 100, 1.5f)]
    [InlineData(100, 900, 1.5f)]
    [InlineData(300, 900, 1.25f)]
    public void ElasticConnectionUsesMaterialStiffnessIndependentlyOfItsNumericalPenalty(
        float stiffness, float penalty, float position)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity,
        });
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX,
            RestRotation = Quaternion.Identity, LinearStiffness = stiffness,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = penalty });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>()
            .Read<BodyMass>().Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Build());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>()
            .ReadWrite<AvbdElasticState>().Build());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, position - 1e-5f, position + 1e-5f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(anchor).Position);
        world.ReleaseQuery(connections);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void GyroscopicPredictionUsesTheFullBodyInertiaTensor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity body = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            InverseInertia = new Matrix4x4(1, 0, 0.5f, 0, 0, 1, 0, 0, 0.5f, 0, 1, 0, 0, 0, 0, 1),
        });
        world.Replace(body, new BodyVelocity { Angular = new Vector3(1, 2, 3) });
        var stages = new ApplyGyroscopicTorque(rowsPerPacket: 64)
            .Then(new PredictBodies(Vector3.Zero, rowsPerPacket: 64))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.7666667f, 2.5333333f, 2.6333333f),
            world.Read<BodyVelocity>(body).Angular), 0, 1e-5f);
    }

    [Fact]
    public void OffCenterConnectionCouplesTranslationRotationAndTheFullInertiaTensor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, new Vector3(2, 1, 0), new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, CenterOfMass = Vector3.UnitX,
            InverseInertia = new Matrix4x4(1, 0, 0.5f, 0, 0, 1, 0, 0, 0.5f, 0, 1, 0, 0, 0, 0, 1),
        });
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
        {
            SecondAnchor = Vector3.UnitY,
            RestRotation = Quaternion.Identity, LinearStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>()
            .Read<BodyMass>().Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Build());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>()
            .ReadWrite<AvbdElasticState>().Build());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 1,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        BodyPose pose = world.Read<BodyPose>(moving);
        BodyMass mass = world.Read<BodyMass>(moving);
        // Closed-form Schur complement for one point spring plus the six-dimensional inertial energy.
        Assert.InRange(Vector3.Distance(new Vector3(1.7243428f, -0.2230256f, 0.0609710f),
            pose.GetCenterOfMass(in mass)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(new Quaternion(-0.0918891f, 0.0295358f, -0.2280818f, 0.9688461f),
            pose.Rotation)), 0.99999f, 1.00001f);
        world.ReleaseQuery(connections);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void UserEnergyAndBuiltInConnectionAccumulateInTheSameBodySolve()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity,
        });
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX,
            RestRotation = Quaternion.Identity, LinearStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 900 });
        Entity target = world.CreateEntity(new UserElasticTarget
        {
            Body = moving, Position = new Vector3(4, 0, 0), Stiffness = 100,
        });
        world.Add(target, new UserElasticState { Penalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>()
            .Read<BodyMass>().Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Build());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>()
            .ReadWrite<AvbdElasticState>().Build());
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<UserElasticTarget>()
            .ReadWrite<UserElasticState>().Build());
        var constraints = AvbdConstraintGroups.Combine(
            new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections),
            new AvbdConstraints<UserElasticTarget, UserElasticTargetMath>(targets));
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 2.33332f, 2.33335f);
        world.ReleaseQuery(targets);
        world.ReleaseQuery(connections);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AngularElasticityUsesTheConnectionsRelativeRestRotation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity,
        });
        world.Replace(moving, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f) });
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
        {
            RestRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f), AngularStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { AngularPenalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>()
            .Read<BodyMass>().Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Build());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>()
            .ReadWrite<AvbdElasticState>().Build());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Vector3 direction = Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(moving).Rotation);
        Assert.InRange(Vector3.Distance(new Vector3(0.8775826f, 0.4794255f, 0), direction), 0, 1e-5f);
        world.ReleaseQuery(connections);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void RetargetedAndDestroyedConnectionsFollowTheirEcsRelations()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity firstAnchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity nextAnchor = CreateBody(world, new Vector3(4, 0, 0), new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity,
        });
        RelationEdge<ElasticConnection> connection = world.CreateRelation(firstAnchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX, RestRotation = Quaternion.Identity, LinearStiffness = 100,
        });
        world.Add(connection.Entity, new AvbdElasticState { LinearPenalty = 900 });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>()
            .Read<BodyMass>().Read<BodyStepState>().ReadWrite<AvbdBodyEquation>().Build());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>()
            .Read<UndirectedRelationEndpoints<ElasticConnection>>().ReadWrite<AvbdElasticState>().Build());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);
        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        world.RetargetRelationImmediate(connection, nextAnchor, moving);
        physics.Schedule(number: 1, startTime: 0.1, duration: 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 2.9999f, 3.0001f);

        world.DestroyEntity(nextAnchor);
        physics.Schedule(number: 2, startTime: 0.2, duration: 0.1).Complete();

        Assert.False(world.IsAlive(connection.Entity));
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 4.4999f, 4.5001f);
        world.ReleaseQuery(connections);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position, BodyMass mass)
    {
        Entity body = world.CreateEntity();
        world.Add(body, new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, mass);
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}

internal struct UserElasticTarget : IComponent
{
    public Entity Body;
    public Vector3 Position;
    public float Stiffness;
}

internal struct UserElasticState : IComponent
{
    public Vector3 Multiplier;
    public float Penalty;
}

internal readonly struct UserElasticTargetMath : IAvbdConstraint<UserElasticTarget>
{
    public static int GetBodyCount(in UserElasticTarget constraint, scoped QueryRow row) => 1;
    public static Entity GetBody(in UserElasticTarget constraint, scoped QueryRow row, int index) => constraint.Body;

    public static void Initialize(in UserElasticTarget constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) =>
        row.ReadWrite<UserElasticState>().Multiplier *= retention;

    public static void AccumulateBodyEquation(in UserElasticTarget constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        ref readonly UserElasticState state = ref row.ReadRef<UserElasticState>();
        var material = new AvbdMaterialTerms(constraint.Stiffness, state.Penalty);
        Vector3 force = material.EvaluateForce(pose.GetCenterOfMass(in mass) - constraint.Position, state.Multiplier);
        equation.AddRow(Vector3.UnitX, Vector3.Zero, force.X, material.EffectiveStiffness);
        equation.AddRow(Vector3.UnitY, Vector3.Zero, force.Y, material.EffectiveStiffness);
        equation.AddRow(Vector3.UnitZ, Vector3.Zero, force.Z, material.EffectiveStiffness);
    }

    public static void UpdateDuals(in UserElasticTarget constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly BodyPose pose = ref bodies.ReadPose(constraint.Body);
        ref readonly BodyMass mass = ref bodies.ReadMass(constraint.Body);
        ref UserElasticState state = ref row.ReadWrite<UserElasticState>();
        var material = new AvbdMaterialTerms(constraint.Stiffness, state.Penalty);
        state.Multiplier = material.EvaluateForce(pose.GetCenterOfMass(in mass) - constraint.Position, state.Multiplier);
    }
}
