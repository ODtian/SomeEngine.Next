using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsScaleSceneTests
{
    [Fact]
    public void ThePyramidArrayCreates88200BricksAndOneImpactBodyForEachStructure()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "pyramids-88200");
        using var scene = PhysicsScaleScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60));

        Assert.Equal(88621, scene.Bodies.Length);
        Assert.Equal(new Vector3(.5f), world.Read<BoxGeometry>(scene.Bodies.Span[1]).HalfExtents);
        Assert.Equal(new Vector3(-238, -60, .5f), world.Read<BodyPose>(scene.Bodies.Span[1]).Position);
        Assert.Equal(new Vector3(-228.5f, -60, 19.5f), world.Read<BodyPose>(scene.Bodies.Span[210]).Position);
        Assert.Equal(new Vector3(-214, -60, .5f), world.Read<BodyPose>(scene.Bodies.Span[211]).Position);
        Assert.Equal(new Vector3(227.5f, 60, 19.5f), world.Read<BodyPose>(scene.Bodies.Span[88200]).Position);
        var firstImpactBody = scene.Bodies.Span[88201];
        Assert.Equal(new Vector3(-241, -60, 1), world.Read<BodyPose>(firstImpactBody).Position);
        world.ExecuteBufferRead<BodyImpulse>(scene.Bodies.Span[^1], impulses =>
        {
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(12500, 0, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
        });
    }

    [Fact]
    public void TheContinuousBrickSceneCreates110000BricksAndItsTimedImpactBody()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "bricks-110000");
        using var scene = PhysicsScaleScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60));

        Assert.Equal(110002, scene.Bodies.Length);
        var ground = scene.Bodies.Span[0];
        var firstBrick = scene.Bodies.Span[1];
        var nextLayer = scene.Bodies.Span[10001];
        var highestBrick = scene.Bodies.Span[110000];
        var impactBody = scene.Bodies.Span[^1];
        Assert.Equal(BodyMotion.Static, world.Read<BodyMass>(ground).Motion);
        Assert.Equal(1, world.Read<BodyMass>(firstBrick).InverseMass);
        Assert.Equal(new Vector3(.5f, .25f, .25f), world.Read<BoxGeometry>(firstBrick).HalfExtents);
        Assert.Equal(new Vector3(-49.5f, -24.75f, .25f), world.Read<BodyPose>(firstBrick).Position);
        Assert.Equal(new Vector3(-49, -24.75f, .75f), world.Read<BodyPose>(nextLayer).Position);
        Assert.Equal(5.25f, world.Read<BodyPose>(highestBrick).Position.Z);
        Assert.Equal(.002f, world.Read<BodyMass>(impactBody).InverseMass);
        world.ExecuteBufferRead<BodyImpulse>(impactBody, impulses =>
        {
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(12500, 0, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
        });
    }
}
