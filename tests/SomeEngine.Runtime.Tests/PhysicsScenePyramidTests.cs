using DefaultGame;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsScenePyramidTests
{
    [Fact]
    public void TheOriginalFallingPyramidDissipatesEnergyThroughItsCompletedContactFrames()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "2d-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        double initial = ReadEnergy(world, scene);

        for (int frame = 0; frame < 180; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();

            double energy = ReadEnergy(world, scene);
            Assert.True(energy <= initial * 1.1, $"Step {frame}: mechanical energy {energy}, initial energy {initial}.");
        }
    }

    [Fact]
    public void TheOriginalSpatialFallingPyramidDissipatesEnergyAndSettlesThroughItsCompletedContactFrames()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        double initial = ReadEnergy(world, scene);

        for (int frame = 0; frame < 180; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();

            double energy = ReadEnergy(world, scene);
            Assert.True(energy <= initial * 1.1, $"Step {frame}: mechanical energy {energy}, initial energy {initial}.");
        }

        Assert.InRange(ReadEnergy(world, scene), 1614.9, 1615.1);
        foreach (var body in scene.Bodies.Span[1..])
            Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .01f);
    }

    private static double ReadEnergy(World world, PhysicsSceneSimulation scene)
    {
        double energy = 0;
        Vector3 up = scene.Description.Dimension == 2 ? Vector3.UnitY : Vector3.UnitZ;
        foreach (var entity in scene.Bodies.Span)
        {
            BodyMass mass = world.Read<BodyMass>(entity);
            if (mass.Motion != BodyMotion.Dynamic) continue;
            BodyPose pose = world.Read<BodyPose>(entity);
            BodyVelocity velocity = world.Read<BodyVelocity>(entity);
            Vector3 spin = Vector3.Transform(velocity.Angular, Quaternion.Conjugate(pose.Rotation));
            double rotational = 0;
            if (mass.InverseInertia.M11 > 0) rotational += spin.X * spin.X / mass.InverseInertia.M11;
            if (mass.InverseInertia.M22 > 0) rotational += spin.Y * spin.Y / mass.InverseInertia.M22;
            if (mass.InverseInertia.M33 > 0) rotational += spin.Z * spin.Z / mass.InverseInertia.M33;
            energy += .5 / mass.InverseMass * velocity.Linear.LengthSquared()
                + .5 * rotational
                + 10 / mass.InverseMass * (Vector3.Dot(pose.GetCenterOfMass(in mass), up) + 2);
        }
        return energy;
    }
}
