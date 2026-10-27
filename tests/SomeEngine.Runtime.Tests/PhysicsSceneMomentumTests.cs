using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneMomentumTests
{
    [Fact]
    public void TheOriginalFreeChainRespectsItsMomentumAndEnergyAfterTheTimedImpulse()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-free-chain");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        PhysicsStepResult result = default;
        double initialEnergy = ReadEnergy(world, scene), maximumEnergy = initialEnergy;

        for (int number = 0; number < 211; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
            maximumEnergy = Math.Max(maximumEnergy, ReadEnergy(world, scene));
        }

        Vector3 momentum = default;
        foreach (var body in scene.Bodies.Span)
            momentum += world.Read<BodyVelocity>(body).Linear / world.Read<BodyMass>(body).InverseMass;

        // Twenty .25 kg bodies fall for 211 fixed steps; the timed last-body impulse adds .5 kg·m/s in Y.
        Assert.Equal(210, result.Number);
        Assert.InRange(Vector3.Distance(momentum, new(0, .5f, -175.83334f)), 0, .01f);
        // The transverse impulse adds .5 J to the falling chain's initially vertical motion.
        Assert.InRange(maximumEnergy, 0, initialEnergy + .5);
    }

    private static double ReadEnergy(World world, PhysicsSceneSimulation scene)
    {
        double energy = 0;
        foreach (var body in scene.Bodies.Span)
        {
            BodyMass mass = world.Read<BodyMass>(body);
            BodyPose pose = world.Read<BodyPose>(body);
            BodyVelocity velocity = world.Read<BodyVelocity>(body);
            Vector3 spin = Vector3.Transform(velocity.Angular, Quaternion.Conjugate(pose.Rotation));
            energy += .5 / mass.InverseMass * velocity.Linear.LengthSquared()
                + .5 * (spin.X * spin.X / mass.InverseInertia.M11 + spin.Y * spin.Y / mass.InverseInertia.M22
                    + spin.Z * spin.Z / mass.InverseInertia.M33)
                + 10 / mass.InverseMass * (pose.GetCenterOfMass(in mass).Z + 2);
        }
        return energy;
    }
}
