using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneLandingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.02f)]
    public void TheOriginalFaceDropSettlesAtItsGroundSurfaceThroughThirtySixCompletedFrames(float contactDistance)
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60) { ContactDistance = contactDistance });
        var body = scene.Bodies.Span[1];
        PhysicsStepResult result = default;

        for (int frame = 0; frame < 36; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds);
            result = scene.CompleteForRead();
        }

        Assert.InRange(world.Read<BodyPose>(body).Position.Z, .3999f, .4001f);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Z, -1e-4f, 1e-4f);
        Assert.Equal(world.Read<BodyPose>(body).Position, world.Read<LocalTransform>(body).Value.Position);
        Assert.Equal(35, result.Number);
        Assert.Equal(.6, result.EndTime, 12);
        Assert.Equal(35, world.Read<BodyStepState>(body).Number);
    }
}
