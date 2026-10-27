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
    public void TheOriginalHangingHeavyChainRespectsItsInitialMechanicalEnergy()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-chain");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        PhysicsStepResult result = default;
        double maximumEnergy = ReadEnergy(world, scene);

        for (int number = 0; number < 180; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
            maximumEnergy = Math.Max(maximumEnergy, ReadEnergy(world, scene));
        }

        Assert.Equal(179, result.Number);
        // Eighteen .25 kg links and one 125 kg load begin at z=10, with potential zero at z=-2.
        Assert.InRange(maximumEnergy, 0, 129.5 * 10 * 12);
    }

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

    [Theory]
    [InlineData("3d-oblique-collisions", 180)]
    [InlineData("3d-release-chain", 211)]
    [InlineData("3d-branched-chain", 211)]
    public void OriginalSceneBodiesKeepTheirPoseAndVelocityFinite(string sceneId, int steps)
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == sceneId);
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);

        for (int number = 0; number < steps; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            PhysicsStepResult result = scene.CompleteForRead();
            foreach (var body in scene.Bodies.Span)
            {
                BodyPose pose = world.Read<BodyPose>(body);
                BodyVelocity velocity = world.Read<BodyVelocity>(body);
                Assert.True(IsFinite(pose.Position) && IsFinite(pose.Rotation)
                    && IsFinite(velocity.Linear) && IsFinite(velocity.Angular),
                    $"Step {result.Number}, body {body.Index}: position={pose.Position}, rotation={pose.Rotation}, linear={velocity.Linear}, angular={velocity.Angular}");
            }
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static double ReadEnergy(World world, PhysicsSceneSimulation scene)
    {
        double energy = 0;
        foreach (var body in scene.Bodies.Span)
        {
            BodyMass mass = world.Read<BodyMass>(body);
            if (mass.Motion != BodyMotion.Dynamic) continue;
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
