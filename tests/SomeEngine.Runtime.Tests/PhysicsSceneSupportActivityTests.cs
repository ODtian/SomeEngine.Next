using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneSupportActivityTests
{
    [Fact]
    public void TheOriginalRestingSpatialPyramidStaysAsleepAfterItsSupportLoadsSettle()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var description = PhysicsScenes.All.Single(scene => scene.Id == "3d-resting-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        for (int frame = 0; frame < 60; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();
            if (frame >= 29)
                foreach (var body in scene.Bodies.Span[1..])
                    Assert.True(world.Read<BodyActivity>(body).Sleeping);
        }
    }
}
