using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsScenePlatformTests
{
    [Fact]
    public void SceneSelectionUsesTheNameplatesAssociatedWithItsContent()
    {
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        TestScene physics = PhysicsSceneExample.CreateCpu(description).CreateScene(".", new StandardMaterial());
        var platform = new SceneTestPlatform([new TestScene("spheres", "."), physics],
            RuntimeStartupOptions.Parse([]) with { WorldNameplates = true });
        Assert.True(platform.Application.Options.WorldNameplates);

        platform.SelectScene(1);

        Assert.False(platform.Application.Options.WorldNameplates);
        platform.SelectScene(0);
        Assert.True(platform.Application.Options.WorldNameplates);
    }

    [Fact]
    public void TheOriginalSceneFeedsItsCompletedBodyThroughTheGameFrameTransformStage()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var frames = new GameFrameSystems(world, includeInterface: false);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 8, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
        var body = scene.Bodies.Span[1];
        world.Add(body, new WorldTransform());
        frames.Physics = Assert.IsAssignableFrom<IGameFramePhysics>(scene);

        frames.UpdateFrame(scene.StepSeconds);

        Assert.InRange(world.Read<BodyPose>(body).Position.Z, 1.3972221f, 1.3972223f);
        Assert.Equal(world.Read<BodyPose>(body).Position, world.Read<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(new Vector3(3, 2, .8f), world.Read<WorldTransform>(body).Qvvs.Stretch);
    }
}
