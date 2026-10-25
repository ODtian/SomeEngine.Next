using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class JointConstraintTests
{
    [Fact]
    public void AvbdFixedJointAlignsBothLocalFrames()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity moving = CreateBody(world, new BodyPose { Position = new Vector3(3, 4, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var firstFrame = new JointFrame(new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        var secondFrame = new JointFrame(Vector3.UnitX, Quaternion.Identity);
        var joint = world.CreateRelation(anchor, moving, JointConstraints.CreateFixed(firstFrame, secondFrame, 1e6f, 1e6f));
        world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f), AngularPenalty = new Vector3(1e6f) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        BodyPose result = world.Read<BodyPose>(moving);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, result.Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, result.Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdFixedJointAlignsBothLocalFrames()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity moving = CreateBody(world, new BodyPose { Position = new Vector3(3, 4, 0), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var sourceFrame = new JointFrame(new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        var targetFrame = new JointFrame(Vector3.UnitX, Quaternion.Identity);
        var joint = world.CreateRelation(anchor, moving, JointConstraints.CreateFixed(sourceFrame, targetFrame, 1e6f, 1e6f));
        world.Add(joint.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        BodyPose result = world.Read<BodyPose>(moving);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, result.Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, result.Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdBallJointSharesTheConnectionForceAndPreservesFreeRotation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(3, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Angular = new Vector3(0.4f, 0, 0) });
        world.Replace(target, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 3, InverseInertia = Matrix4x4.Identity });
        var joint = world.CreateRelation(source, target, JointConstraints.CreateBall(
            new JointFrame(Vector3.UnitX, Quaternion.Identity), JointFrame.Identity, 100));
        world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(900) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(source).Position.X, 0.39999f, 0.40001f);
        Assert.InRange(world.Read<BodyPose>(target).Position.X, 1.79999f, 1.80001f);
        Assert.InRange(Vector3.Distance(new Vector3(0.9553365f, 0, -0.2955202f),
            Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(target).Rotation)), 0, 1e-5f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdBallJointSharesTheConnectionForceAndPreservesFreeRotation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(3, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f),
        }, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Angular = new Vector3(0.4f, 0, 0) });
        world.Replace(target, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 3, InverseInertia = Matrix4x4.Identity });
        var joint = world.CreateRelation(source, target, JointConstraints.CreateBall(
            new JointFrame(Vector3.UnitX, Quaternion.Identity), JointFrame.Identity, 100));
        world.Add(joint.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(world.Read<BodyPose>(source).Position.X, 0.39999f, 0.40001f);
        Assert.InRange(world.Read<BodyPose>(target).Position.X, 1.79999f, 1.80001f);
        Assert.InRange(Vector3.Distance(new Vector3(0.9553365f, 0, -0.2955202f),
            Vector3.Transform(Vector3.UnitX, world.Read<BodyPose>(target).Rotation)), 0, 1e-5f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdHingeLimitsItsFreeAxisWhenTheLimitIsEnabled()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f) }, BodyMotion.Dynamic);
        world.Replace(target, new BodyVelocity { Angular = Vector3.UnitX });
        JointConstraint parameters = JointConstraints.CreateHinge(JointFrame.Identity, JointFrame.Identity, 1e6f, 1e6f);
        var joint = world.CreateRelation(source, target, parameters);
        world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f), AngularPenalty = new Vector3(1e6f) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, bodyIterations: 3,
                multiplierRetention: 0, initialErrorFraction: 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.7648422f, 0.6442177f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-4f);

        parameters.AngularX = new JointAxisLimit(JointAxisMotion.Limited, -0.25f, 0.25f);
        world.Replace(joint.Entity, parameters);
        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.9689124f, 0.2474040f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdHingeLimitsItsFreeAxisWhenTheLimitIsEnabled()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f) }, BodyMotion.Dynamic);
        world.Replace(target, new BodyVelocity { Angular = Vector3.UnitX });
        JointConstraint parameters = JointConstraints.CreateHinge(JointFrame.Identity, JointFrame.Identity, 1e6f, 1e6f);
        var joint = world.CreateRelation(source, target, parameters);
        world.Add(joint.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.7648422f, 0.6442177f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-4f);

        parameters.AngularX = new JointAxisLimit(JointAxisMotion.Limited, -0.25f, 0.25f);
        world.Replace(joint.Entity, parameters);
        physics.Schedule(1, 0.1, 0.1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 0.9689124f, 0.2474040f),
            Vector3.Transform(Vector3.UnitY, world.Read<BodyPose>(target).Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdSliderPreservesFrameXTravelAndAlignsTheOtherCoordinates()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(2, 2, 2), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f),
        }, BodyMotion.Dynamic);
        var joint = world.CreateRelation(source, target, JointConstraints.CreateSlider(JointFrame.Identity, JointFrame.Identity, 1e6f, 1e6f));
        world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f), AngularPenalty = new Vector3(1e6f) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        BodyPose result = world.Read<BodyPose>(target);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), result.Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, result.Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdSliderPreservesFrameXTravelAndAlignsTheOtherCoordinates()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(2, 2, 2), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f),
        }, BodyMotion.Dynamic);
        var joint = world.CreateRelation(source, target, JointConstraints.CreateSlider(JointFrame.Identity, JointFrame.Identity, 1e6f, 1e6f));
        world.Add(joint.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        BodyPose result = world.Read<BodyPose>(target);
        Assert.InRange(Vector3.Distance(new Vector3(2, 0, 0), result.Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, result.Rotation)), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void AvbdGeneralJointAppliesBothBoundsInTheReferenceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity positive = CreateBody(world, new BodyPose { Position = new Vector3(2, 3, 4), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity negative = CreateBody(world, new BodyPose { Position = new Vector3(2, -3, 4), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var parameters = new JointConstraint
        {
            SourceFrame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
            TargetFrame = JointFrame.Identity,
            LinearX = new JointAxisLimit(JointAxisMotion.Limited, -1, 1),
            LinearY = JointAxisLimit.Locked,
            LinearStiffness = new Vector3(1e6f),
        };
        var upper = world.CreateRelation(source, positive, parameters);
        var lower = world.CreateRelation(source, negative, parameters);
        world.Add(upper.Entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f) });
        world.Add(lower.Entity, new AvbdJointState { LinearPenalty = new Vector3(1e6f) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, constraints, 32, 0, 0)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 1, 4), world.Read<BodyPose>(positive).Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(new Vector3(0, -1, 4), world.Read<BodyPose>(negative).Position), 0, 1e-3f);
        world.ReleaseQuery(joints);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdGeneralJointAppliesBothBoundsInTheReferenceFrame()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity positive = CreateBody(world, new BodyPose { Position = new Vector3(2, 3, 4), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        Entity negative = CreateBody(world, new BodyPose { Position = new Vector3(2, -3, 4), Rotation = Quaternion.Identity }, BodyMotion.Dynamic);
        var parameters = new JointConstraint
        {
            SourceFrame = new JointFrame(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
            TargetFrame = JointFrame.Identity,
            LinearX = new JointAxisLimit(JointAxisMotion.Limited, -1, 1),
            LinearY = JointAxisLimit.Locked,
            LinearStiffness = new Vector3(1e6f),
        };
        var upper = world.CreateRelation(source, positive, parameters);
        var lower = world.CreateRelation(source, negative, parameters);
        world.Add(upper.Entity, new XpbdJointState());
        world.Add(lower.Entity, new XpbdJointState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var constraints = new XpbdConstraints<JointConstraint, XpbdJointProjection>(joints);
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, constraints, 32)).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        physics.Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(0, 1, 4), world.Read<BodyPose>(positive).Position), 0, 1e-3f);
        Assert.InRange(Vector3.Distance(new Vector3(0, -1, 4), world.Read<BodyPose>(negative).Position), 0, 1e-3f);
        world.ReleaseQuery(joints);
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
