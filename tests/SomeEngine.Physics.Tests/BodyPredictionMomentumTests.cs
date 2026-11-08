using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using Xunit;

namespace SomeEngine.Physics.Tests;

public sealed class BodyPredictionMomentumTests
{
    [Fact]
    public void KinematicVelocityTargetsKeepTheirAngularSpeedAcrossMultipleTurns()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Kinematic });
        world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState());
        Vector3 angular = new(0, 0, 200 * MathF.PI);
        world.AddBuffer<BodyVelocityTarget>(body);
        world.ExecuteBufferWrite<BodyVelocityTarget>(body, targets => targets.Add(new() { Angular = angular, Time = 0 }));
        var stages = new ApplyBodyInputs(64).Then(new PredictBodies(Vector3.Zero, 64)).Then(new UpdateBodyVelocities(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, 1d / 60).Complete();
        simulation.Schedule(1, 1d / 60, 1d / 60).Complete();

        Assert.InRange(Vector3.Distance(angular, world.Read<BodyVelocity>(body).Angular), 0, 1e-4f);
    }

    [Fact]
    public void FreeRotationKeepsItsAngularVelocityAcrossMultipleTurns()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        Vector3 angular = new(0, 0, 200 * MathF.PI);
        world.Add(body, new BodyVelocity { Angular = angular });
        world.Add(body, new BodyStepState());
        var stages = new PredictBodies(Vector3.Zero, 64).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1d / 60).Complete();

        Assert.InRange(Vector3.Distance(angular, world.Read<BodyVelocity>(body).Angular), 0, 1e-4f);
    }

    [Fact]
    public void GravityKeepsTheCompletedFreeBodyMomentum()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var body = world.CreateEntity();
        world.Add(body, new BodyPose { Position = new(0, 0, 10), Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        var stages = new PredictBodies(new(0, 0, -10), 64).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);
        const float duration = 1f / 60;
        for (int number = 0; number < 211; number++)
            physics.Schedule(number, number * (double)duration, duration).Complete();

        Vector3 momentum = world.Read<BodyVelocity>(body).Linear;
        Vector3 expected = new(0, 0, -10 * 211 * duration);
        float error = Vector3.Distance(momentum, expected);
        Assert.True(error <= 1e-4f, $"Completed momentum {momentum}; gravity momentum {expected}; error {error:R}.");
    }
}
