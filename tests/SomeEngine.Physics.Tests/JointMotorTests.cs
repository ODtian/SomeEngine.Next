using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class JointMotorTests
{
    [Fact]
    public void DisablingAnXpbdMotorClearsItsPreviouslyAppliedLoad()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var joint = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(joint.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            LinearStiffness = new(100, 0, 0),
            MaximumForce = new(100)
        });
        world.Add(joint.Entity, new JointMotorTarget { Frame = new JointFrame(Vector3.UnitX, Quaternion.Identity) });
        world.Add(joint.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>().WithStored<BodyVelocity>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>().WithStored<RelationEndpoints<JointConstraint>>()
            .WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var solve = SolveXpbd.Create(world, bodies, new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors), 8);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));
        var simulation = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);
        simulation.Schedule(0, 0, .1).Complete();
        Assert.True(world.ReadComponentData<XpbdMotorState>(joint.Entity).LinearMultiplier.Length() > 0);
        world.Replace(joint.Entity, new JointMotor { Space = JointMotorSpace.Source, Response = JointMotorResponse.Target, MaximumForce = new(100) });

        simulation.Schedule(1, .1, .1).Complete();

        Assert.Equal(Vector3.Zero, world.ReadComponentData<XpbdMotorState>(joint.Entity).LinearMultiplier);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<XpbdMotorState>(joint.Entity).AngularMultiplier);
    }

    [Fact]
    public void AvbdLimitedMotorConvergesAfterCrossingItsForceLimit()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Position = new(.01f, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var joint = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(joint.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            LinearStiffness = new(100000, 0, 0),
            MaximumForce = new(100),
        });
        world.Add(joint.Entity, new JointMotorTarget { Frame = JointFrame.Identity });
        world.Add(joint.Entity, new AvbdMotorState { LinearPenalty = new(100000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var solve = SolveAvbd.Create(world, bodies, new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors), 32, 0, 0);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1).Schedule(0, 0, 1d / 60).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(target).Position.X, .0003473f, .0003477f);
    }

    [Fact]
    public void AvbdMotorUsesTimedParentTargetWithSpringAndDamping()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
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
            LinearVelocity = new Vector3(2, 0, 0),
            Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(10, 0.4666667f, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0, 4.666667f, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void XpbdMotorUsesTimedParentTargetWithSpringAndDamping()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
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
            LinearVelocity = new Vector3(2, 0, 0),
            Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(10, 0.4666667f, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0, 4.666667f, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void AvbdMotorSamplesWorldTargetsAtEachSubstepWhileTheReferenceMoves()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = Vector3.UnitX });
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0),
            MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = Vector3.UnitX, Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 2);

        physics.Schedule(0, 0, 0.2).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.15f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(10.2f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void XpbdMotorSamplesWorldTargetsAtEachSubstepWhileTheReferenceMoves()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose
        {
            Position = new Vector3(10, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = Vector3.UnitX });
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(100, 0, 0),
            MaximumForce = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = Vector3.UnitX, Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 2);

        physics.Schedule(0, 0, 0.2).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0.15f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(10.2f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void AvbdMotorCapsTheVelocityDriveAndFeedsTheReactionToTheSource()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = JointFrame.Identity,
            LinearVelocity = new Vector3(10, 0, 0),
            Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.2f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void XpbdMotorCapsTheVelocityDriveAndFeedsTheReactionToTheSource()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = JointFrame.Identity,
            LinearVelocity = new Vector3(10, 0, 0),
            Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.2f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), world.ReadComponentData<BodyVelocity>(target).Linear), 0, 1e-4f);
    }

    [Fact]
    public void AvbdMotorSamplesAngularPoseAndVelocityInTheSourceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Entity source = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            AngularStiffness = new Vector3(0, 0, 100),
            MaximumTorque = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f)),
            AngularVelocity = Vector3.UnitZ,
            Time = 0,
        });
        world.Add(relation.Entity, new AvbdMotorState { AngularPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, bodyIterations: 3,
                multiplierRetention: 0, initialErrorFraction: 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.0998334f, 0.9950042f, 0),
            Vector3.Transform(Vector3.UnitX, world.ReadComponentData<BodyPose>(target).Rotation)), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, world.ReadComponentData<BodyVelocity>(target).Angular), 0, 1e-3f);
    }

    [Fact]
    public void XpbdMotorSamplesAngularPoseAndVelocityInTheSourceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Entity source = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = rotation }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.Source,
            Response = JointMotorResponse.Target,
            AngularStiffness = new Vector3(0, 0, 100),
            MaximumTorque = new Vector3(100),
        });
        world.Add(relation.Entity, new JointMotorTarget
        {
            Frame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f)),
            AngularVelocity = Vector3.UnitZ,
            Time = 0,
        });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(-0.0998334f, 0.9950042f, 0),
            Vector3.Transform(Vector3.UnitX, world.ReadComponentData<BodyPose>(target).Rotation)), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, world.ReadComponentData<BodyVelocity>(target).Angular), 0, 1e-3f);
    }

    [Fact]
    public void AvbdWorldMotorLimitsTheTorqueOfItsAngularVelocityDrive()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Target,
            AngularDamping = new Vector3(10, 0, 0),
            MaximumTorque = Vector3.One,
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, AngularVelocity = new Vector3(2, 0, 0), Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { AngularPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, bodyIterations: 3,
                multiplierRetention: 0, initialErrorFraction: 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.99995f, 0.009999833f),
            Vector3.Transform(Vector3.UnitY, world.ReadComponentData<BodyPose>(target).Rotation)), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.1f, 0, 0), world.ReadComponentData<BodyVelocity>(target).Angular), 0, 1e-5f);
    }

    [Fact]
    public void XpbdWorldMotorLimitsTheTorqueOfItsAngularVelocityDrive()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Target,
            AngularDamping = new Vector3(10, 0, 0),
            MaximumTorque = Vector3.One,
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, AngularVelocity = new Vector3(2, 0, 0), Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.99995f, 0.009999833f),
            Vector3.Transform(Vector3.UnitY, world.ReadComponentData<BodyPose>(target).Rotation)), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.1f, 0, 0), world.ReadComponentData<BodyVelocity>(target).Angular), 0, 1e-5f);
    }

    [Fact]
    public void AvbdWorldMotorFeedsBackItsLimitedForceWhileKeepingTheWorldTarget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0 });
        world.Add(relation.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<AvbdMotorState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(9.8f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
    }

    [Fact]
    public void XpbdWorldMotorFeedsBackItsLimitedForceWhileKeepingTheWorldTarget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = CreateBody(world, new BodyPose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var relation = Relations<JointConstraint>.CreateEdge(world, source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity,
            TargetFrame = JointFrame.Identity,
        });
        world.Add(relation.Entity, new JointMotor
        {
            Space = JointMotorSpace.World,
            Response = JointMotorResponse.Both,
            LinearDamping = new Vector3(10, 0, 0),
            MaximumForce = new Vector3(20),
        });
        world.Add(relation.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = new Vector3(10, 0, 0), Time = 0 });
        world.Add(relation.Entity, new XpbdMotorState());
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query motors = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<JointMotor>().WithStored<JointMotorTarget>().WithStored<XpbdMotorState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointMotorProjection>(motors);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(9.8f, 0, 0), world.ReadComponentData<BodyPose>(source).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.2f, 0, 0), world.ReadComponentData<BodyPose>(target).Position), 0, 1e-5f);
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
}
