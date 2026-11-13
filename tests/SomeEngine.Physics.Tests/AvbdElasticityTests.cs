using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
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
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            InverseInertia = Matrix4x4.Identity,
        });
        Entity connection = Relations<ElasticConnection>.CreateEdge(world, anchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX,
            RestRotation = Quaternion.Identity,
            LinearStiffness = stiffness,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = penalty });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>()
            .WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>().WithStored<RelationEndpoints<ElasticConnection>>()
            .WithStored<AvbdElasticState>());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.X, position - 1e-5f, position + 1e-5f);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(anchor).Position);
    }

    [Fact]
    public void GyroscopicPredictionUsesTheFullBodyInertiaTensor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
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
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.7666667f, 2.5333333f, 2.6333333f),
            world.ReadComponentData<BodyVelocity>(body).Angular), 0, 1e-5f);
    }

    [Fact]
    public void OffCenterConnectionCouplesTranslationRotationAndTheFullInertiaTensor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, new Vector3(2, 1, 0), new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            CenterOfMass = Vector3.UnitX,
            InverseInertia = new Matrix4x4(1, 0, 0.5f, 0, 0, 1, 0, 0, 0.5f, 0, 1, 0, 0, 0, 0, 1),
        });
        Entity connection = Relations<ElasticConnection>.CreateEdge(world, anchor, moving, new ElasticConnection
        {
            SecondAnchor = Vector3.UnitY,
            RestRotation = Quaternion.Identity,
            LinearStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>()
            .WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>().WithStored<RelationEndpoints<ElasticConnection>>()
            .WithStored<AvbdElasticState>());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 1,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        BodyPose pose = world.ReadComponentData<BodyPose>(moving);
        BodyMass mass = world.ReadComponentData<BodyMass>(moving);
        // Closed-form Schur complement for one point spring plus the six-dimensional inertial energy.
        Assert.InRange(Vector3.Distance(new Vector3(1.7243428f, -0.2230256f, 0.0609710f),
            pose.GetCenterOfMass(in mass)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(new Quaternion(-0.0918891f, 0.0295358f, -0.2280818f, 0.9688461f),
            pose.Rotation)), 0.99999f, 1.00001f);
    }

    [Fact]
    public void UserEnergyAndBuiltInConnectionAccumulateInTheSameBodySolve()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            InverseInertia = Matrix4x4.Identity,
        });
        Entity connection = Relations<ElasticConnection>.CreateEdge(world, anchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX,
            RestRotation = Quaternion.Identity,
            LinearStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 900 });
        Entity target = world.Create(new UserElasticTarget
        {
            Body = moving,
            Position = new Vector3(4, 0, 0),
            Stiffness = 100,
        });
        world.Add(target, new UserElasticState { Penalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>()
            .WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>().WithStored<RelationEndpoints<ElasticConnection>>()
            .WithStored<AvbdElasticState>());
        Query targets = world.Query(new QueryDescription().WithStored<UserElasticTarget>()
            .WithStored<UserElasticState>());
        var constraints = AvbdConstraintGroups.Combine(
            new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections),
            new AvbdConstraints<UserElasticTarget, UserElasticTargetMath>(targets));
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.X, 2.33332f, 2.33335f);
    }

    [Fact]
    public void AngularElasticityUsesTheConnectionsRelativeRestRotation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity anchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            InverseInertia = Matrix4x4.Identity,
        });
        world.Replace(moving, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f) });
        Entity connection = Relations<ElasticConnection>.CreateEdge(world, anchor, moving, new ElasticConnection
        {
            RestRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f),
            AngularStiffness = 100,
        }).Entity;
        world.Add(connection, new AvbdElasticState { AngularPenalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>()
            .WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>().WithStored<RelationEndpoints<ElasticConnection>>()
            .WithStored<AvbdElasticState>());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        Vector3 direction = Vector3.Transform(Vector3.UnitX, world.ReadComponentData<BodyPose>(moving).Rotation);
        Assert.InRange(Vector3.Distance(new Vector3(0.8775826f, 0.4794255f, 0), direction), 0, 1e-5f);
    }

    [Fact]
    public void RetargetedAndDestroyedConnectionsFollowTheirEcsRelations()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity firstAnchor = CreateBody(world, Vector3.Zero, new BodyMass { Motion = BodyMotion.Static });
        Entity nextAnchor = CreateBody(world, new Vector3(4, 0, 0), new BodyMass { Motion = BodyMotion.Static });
        Entity moving = CreateBody(world, new Vector3(2, 0, 0), new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            InverseInertia = Matrix4x4.Identity,
        });
        RelationEdge<ElasticConnection> connection = Relations<ElasticConnection>.CreateEdge(world, firstAnchor, moving, new ElasticConnection
        {
            FirstAnchor = Vector3.UnitX,
            RestRotation = Quaternion.Identity,
            LinearStiffness = 100,
        });
        world.Add(connection.Entity, new AvbdElasticState { LinearPenalty = 900 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>()
            .WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>()
            .WithStored<RelationEndpoints<ElasticConnection>>().WithStored<AvbdElasticState>());
        var constraints = new AvbdConstraints<ElasticConnection, AvbdElasticConnectionMath>(connections);
        var solve = SolveAvbd.Create(world, bodies, constraints, iterations: 32,
            multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(solve).Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);
        physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();

        world.RetargetRelationImmediate(connection, nextAnchor, moving);
        physics.Schedule(number: 1, startTime: 0.1, duration: 0.1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.X, 2.9999f, 3.0001f);

        world.Destroy(nextAnchor);
        physics.Schedule(number: 2, startTime: 0.2, duration: 0.1).Complete();

        Assert.False(world.IsAlive(connection.Entity));
        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.X, 4.4999f, 4.5001f);
    }

    private static Entity CreateBody(World world, Vector3 position, BodyMass mass)
    {
        Entity body = world.Create();
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
    private readonly ConstraintColumns<UserElasticTarget, UserElasticState> _fields;
    public UserElasticTargetMath() => _fields = new();
    public static void AddAccesses(World world, Query members, List<JobResourceAccess> accesses) =>
        accesses.AddRange(ConstraintColumns<UserElasticTarget, UserElasticState>.Accesses(world, members));
    public void Bind(World world, Query members) => _fields.Bind(world, members);
    public ref readonly UserElasticTarget ReadConstraint(Entity entity) => ref _fields.ReadConstraint(entity);
    public static int GetBodyCount(in UserElasticTarget constraint, World world, Entity entity, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in UserElasticTarget constraint, World world, Entity entity, int index, in PhysicsStepContext context) => constraint.Body;
    public void Initialize(in UserElasticTarget constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) => _fields.WriteState(entity).Multiplier *= retention;
    public void AccumulateBodyEquation(in UserElasticTarget constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        ref readonly UserElasticState state = ref _fields.ReadState(entity);
        var material = new AvbdMaterialTerms(constraint.Stiffness, state.Penalty);
        Vector3 force = material.EvaluateForce(pose.GetCenterOfMass(in mass) - constraint.Position, state.Multiplier);
        equation.AddRow(Vector3.UnitX, Vector3.Zero, force.X, material.EffectiveStiffness);
        equation.AddRow(Vector3.UnitY, Vector3.Zero, force.Y, material.EffectiveStiffness);
        equation.AddRow(Vector3.UnitZ, Vector3.Zero, force.Z, material.EffectiveStiffness);
    }
    public void UpdateDuals(in UserElasticTarget constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly BodyPose pose = ref bodies.ReadPose(constraint.Body);
        ref readonly BodyMass mass = ref bodies.ReadMass(constraint.Body);
        ref UserElasticState state = ref _fields.WriteState(entity);
        var material = new AvbdMaterialTerms(constraint.Stiffness, state.Penalty);
        state.Multiplier = material.EvaluateForce(pose.GetCenterOfMass(in mass) - constraint.Position, state.Multiplier);
    }
}
