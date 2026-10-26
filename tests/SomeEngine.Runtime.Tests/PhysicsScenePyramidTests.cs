using DefaultGame;
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

    private static double ReadEnergy(World world, PhysicsSceneSimulation scene)
    {
        double energy = 0;
        foreach (var entity in scene.Bodies.Span)
        {
            BodyMass mass = world.Read<BodyMass>(entity);
            if (mass.Motion != BodyMotion.Dynamic) continue;
            BodyPose pose = world.Read<BodyPose>(entity);
            BodyVelocity velocity = world.Read<BodyVelocity>(entity);
            energy += .5 / mass.InverseMass * velocity.Linear.LengthSquared()
                + .5 * velocity.Angular.Z * velocity.Angular.Z / mass.InverseInertia.M33
                + 10 / mass.InverseMass * (pose.GetCenterOfMass(in mass).Y + 2);
        }
        return energy;
    }
}
