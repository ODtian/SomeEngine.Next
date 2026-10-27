using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneTopologyTests
{
    [Fact]
    public void TheOriginalCascadingChainReleasesEachDeclaredConnection()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "joint-release-cascade");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        PhysicsStepResult result = default;

        for (int number = 0; number < 331; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
            int liveJoints = 0;
            foreach (var joint in scene.Joints.Span)
                if (world.IsAlive(joint)) liveJoints++;
            int expected = number < 90 ? 12 : number < 150 ? 11 : number < 210 ? 10 : 9;
            Assert.Equal(expected, liveJoints);
        }

        Assert.Equal(330, result.Number);
    }
}
