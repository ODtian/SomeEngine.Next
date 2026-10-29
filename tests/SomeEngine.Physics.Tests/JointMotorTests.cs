using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class JointMotorTests
{
    [Fact]
    public void AvbdMotorUsesTimedParentTargetWithSpringAndDamping()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0),
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.UnitX, Quaternion.Identity),
            LinearVelocity = new Vector3(2, 0, 0), Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(10, 0.4666667f, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0, 4.666667f, 0), world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorUsesTimedParentTargetWithSpringAndDamping()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0),
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.UnitX, Quaternion.Identity),
            LinearVelocity = new Vector3(2, 0, 0), Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(10, 0.4666667f, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0, 4.666667f, 0), world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdMotorSamplesWorldTargetsAtEachSubstepWhileTheReferenceMoves()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = Vector3.UnitX });
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0), MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = Vector3.UnitX, Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(0, 0, 0.2).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.15f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(10.2f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorSamplesWorldTargetsAtEachSubstepWhileTheReferenceMoves()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = Vector3.UnitX });
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0), MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = Vector3.UnitX, Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(0, 0, 0.2).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.15f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(10.2f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdMotorCapsTheVelocityDriveAndFeedsTheReactionToTheSource()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source, Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0), MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.2f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorCapsTheVelocityDriveAndFeedsTheReactionToTheSource()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source, Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0), MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.2f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdMotorSamplesAngularPoseAndVelocityInTheSourceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Entity source = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source, Response = JointMotorResponse.Target,
            AngularStiffness = new Vector3(0, 0, 100), MaximumTorque = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f)),
            AngularVelocity = Vector3.UnitZ, Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { AngularPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, bodyIterations: 3,
                multiplierRetention: 0, initialErrorFraction: 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.0998334f, 0.9950042f, 0),
            Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(target).Rotation)), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, world.Read<BodyVelocity>(target).Angular), 0, 1e-3f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorSamplesAngularPoseAndVelocityInTheSourceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Entity source = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source, Response = JointMotorResponse.Target,
            AngularStiffness = new Vector3(0, 0, 100), MaximumTorque = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f)),
            AngularVelocity = Vector3.UnitZ, Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.0998334f, 0.9950042f, 0),
            Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(target).Rotation)), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, world.Read<BodyVelocity>(target).Angular), 0, 1e-3f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdWorldMotorLimitsTheTorqueOfItsAngularVelocityDrive()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            AngularDamping = new Vector3(10, 0, 0), MaximumTorque = Vector3.One,
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, AngularVelocity = new Vector3(2, 0, 0), Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { AngularPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, bodyIterations: 3,
                multiplierRetention: 0, initialErrorFraction: 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.99995f, 0.009999833f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.1f, 0, 0), world.Read<BodyVelocity>(target).Angular), 0, 1e-5f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdWorldMotorLimitsTheTorqueOfItsAngularVelocityDrive()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            AngularDamping = new Vector3(10, 0, 0), MaximumTorque = Vector3.One,
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, AngularVelocity = new Vector3(2, 0, 0), Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.99995f, 0.009999833f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.1f, 0, 0), world.Read<BodyVelocity>(target).Angular), 0, 1e-5f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdWorldMotorFeedsBackItsLimitedForceWhileKeepingTheWorldTarget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0), MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(9.8f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdWorldMotorFeedsBackItsLimitedForceWhileKeepingTheWorldTarget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0), MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(9.8f, 0, 0), world.Read<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
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
