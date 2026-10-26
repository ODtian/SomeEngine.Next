using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsJointScaleSceneTests
{
    [Fact]
    public void TheLargeJointGridCreatesIts100000BodiesAndThreeDirectionalConnections()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "joint-grid-100000");
        using var scene = PhysicsScaleScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60));

        Assert.Equal(100001, scene.Bodies.Length);
        Assert.Equal(298701, scene.Joints.Length);
        var anchor = scene.Bodies.Span[1];
        var firstHangingBody = scene.Bodies.Span[401];
        Assert.Equal(BodyMotion.Static, world.Read<BodyMass>(anchor).Motion);
        Assert.Equal(BodyMotion.Dynamic, world.Read<BodyMass>(firstHangingBody).Motion);
        Assert.Equal(new Vector3(.15f), world.Read<BoxGeometry>(firstHangingBody).HalfExtents);
        var vertical = scene.Joints.Span[399];
        var endpoints = world.Read<DirectedRelationEndpoints<JointConstraint>>(vertical);
        Assert.Equal(anchor, endpoints.Source);
        Assert.Equal(firstHangingBody, endpoints.Target);
        JointConstraint joint = world.Read<JointConstraint>(vertical);
        Assert.Equal(new Vector3(0, 0, -.6f), joint.SourceFrame.Position);
        Assert.Equal(new Vector3(0, 0, .6f), joint.TargetFrame.Position);
        var diagonal = world.Read<DirectedRelationEndpoints<JointConstraint>>(scene.Joints.Span[^1]);
        Assert.Equal(scene.Bodies.Span[99599], diagonal.Source);
        Assert.Equal(scene.Bodies.Span[100000], diagonal.Target);
        world.ExecuteBufferRead<BodyImpulse>(scene.Bodies.Span[99801], impulses =>
        {
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(0, 12, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
        });
    }
}
