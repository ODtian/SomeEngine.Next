using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsBreakableScaleSceneTests
{
    [Fact]
    public void TheBreakableStructureCreatesIts100000ConnectedBricksAndTimedImpact()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "breakable-100000");
        using var scene = PhysicsScaleScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60));

        Assert.Equal(100002, scene.Bodies.Length);
        Assert.Equal(298000, scene.Joints.Length);
        Assert.Equal(BodyMotion.Static, world.Read<BodyMass>(scene.Bodies.Span[0]).Motion);
        Assert.Equal(BodyMotion.Dynamic, world.Read<BodyMass>(scene.Bodies.Span[100000]).Motion);
        Assert.Equal(new ConstraintBreakSettings(1000, 200), world.Read<ConstraintBreakSettings>(scene.Joints.Span[0]));
        Assert.Equal(new ConstraintBreakSettings(1000, 200), world.Read<ConstraintBreakSettings>(scene.Joints.Span[^1]));
        world.ExecuteBufferRead<BodyImpulse>(scene.Bodies.Span[^1], impulses =>
        {
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(12500, 0, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
        });
    }
}
