using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class EntityPhysicsStepTests
{
    [Fact]
    public void CompletedStepMovesTheSelectedBodiesAndCarriesTheResultTime()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity();
        world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity { Linear = new Vector3(2, 0, 0) });
        world.Add(body, new BodyMass { InverseMass = 1, InverseInertia = Matrix4x4.Identity, Motion = BodyMotion.Dynamic });
        world.Add(body, new BodyStepState());
        world.AddTag<ParticipatingBody>(body);
        var bodies = new QueryDefinitionBuilder().All<ParticipatingBody>().Build();
        var stages = new PhysicsStages<PredictBodies, UpdateBodyVelocities>(
            new PredictBodies(Vector3.Zero, rowsPerPacket: 64), new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = new PhysicsSimulation<PhysicsStages<PredictBodies, UpdateBodyVelocities>>(
            world, bodies, stages, substeps: 1);

        PhysicsStepResult result = physics.Schedule(number: 7, startTime: 3, duration: 0.5);
        result.Complete();

        Assert.Equal(new Vector3(1, 0, 0), world.Read<BodyPose>(body).Position);
        Assert.Equal(new Vector3(2, 0, 0), world.Read<BodyVelocity>(body).Linear);
        Assert.Equal(7, result.Number);
        Assert.Equal(3.5, result.EndTime);
        Assert.Equal(3.5, world.Read<BodyStepState>(body).Time);
    }

    [Fact]
    public void ForceAndImpulseAffectTheirAssignedSubstepIntervals()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity();
        world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyMass { InverseMass = 0.5f, InverseInertia = Matrix4x4.Identity, Motion = BodyMotion.Dynamic });
        world.Add(body, new BodyStepState());
        world.AddBuffer<BodyForce>(body);
        world.ExecuteBufferWrite<BodyForce>(body, static forces => forces.Add(new BodyForce
        {
            Force = new Vector3(2, 0, 0), StartTime = 0, EndTime = 0.5,
        }));
        world.AddBuffer<BodyImpulse>(body);
        world.ExecuteBufferWrite<BodyImpulse>(body, static impulses => impulses.Add(new BodyImpulse
        {
            Linear = new Vector3(2, 0, 0), Time = 0.5,
        }));
        var stages = new ApplyBodyInputs(rowsPerPacket: 64)
            .Then(new PredictBodies(Vector3.Zero, rowsPerPacket: 64))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        Assert.Equal(new Vector3(1, 0, 0), world.Read<BodyPose>(body).Position);
        Assert.Equal(new Vector3(1.5f, 0, 0), world.Read<BodyVelocity>(body).Linear);
    }

    [Fact]
    public void KinematicTargetSamplesTheSubstepTimeAndRetainsLocalCenterOfMass()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity();
        world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyMass { CenterOfMass = Vector3.UnitX, Motion = BodyMotion.Kinematic });
        world.Add(body, new BodyStepState());
        world.AddBuffer<BodyKinematicTarget>(body);
        world.ExecuteBufferWrite<BodyKinematicTarget>(body, static targets => targets.Add(new BodyKinematicTarget
        {
            StartPose = new BodyPose { Rotation = Quaternion.Identity },
            EndPose = new BodyPose
            {
                Position = Vector3.UnitX,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            },
            StartTime = 0,
            EndTime = 1,
        }));
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(new ApplyKinematicTargets(rowsPerPacket: 64))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(number: 0, startTime: 0, duration: 1).Complete();

        BodyPose pose = world.Read<BodyPose>(body);
        BodyMass mass = world.Read<BodyMass>(body);
        Assert.Equal(Vector3.UnitX, pose.Position);
        Assert.InRange(Vector3.Distance(new Vector3(1, 1, 0), pose.GetCenterOfMass(in mass)), 0, 1e-5f);
        Assert.InRange(world.Read<BodyVelocity>(body).Angular.Z, MathF.PI / 2 - 1e-5f, MathF.PI / 2 + 1e-5f);
    }

    [Fact]
    public void UserStageRunsAfterTheInputDependencyAndBeforeVelocityReconstruction()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity();
        world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyMass { InverseMass = 1, InverseInertia = Matrix4x4.Identity, Motion = BodyMotion.Dynamic });
        world.Add(body, new BodyStepState());
        JobHandle input = ComponentJobAccess<BodyVelocity>.ScheduleWrite(world, new SetBodyVelocity(world, body));
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(new TranslateBodies(Vector3.UnitY))
            .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        PhysicsStepResult result = physics.Schedule(number: 0, startTime: 0, duration: 0.5, input);
        result.Completion.Complete();

        Assert.Equal(new Vector3(1, 1, 0), world.Read<BodyPose>(body).Position);
        Assert.Equal(new Vector3(2, 2, 0), world.Read<BodyVelocity>(body).Linear);
    }
}

public struct ParticipatingBody : ITag;

internal readonly struct SetBodyVelocity(World world, Entity body) : IJob
{
    public void Execute() => world.Replace(body, new BodyVelocity { Linear = new Vector3(2, 0, 0) });
}

internal readonly struct TranslateBodies(Vector3 translation) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new TranslateBodyJob(translation).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: 64, filter: context.Bodies), dependency);
}

internal readonly partial struct TranslateBodyJob(Vector3 translation) : IJobEntity
{
    public void Execute(ref BodyPose pose) => pose.Position += translation;
}
