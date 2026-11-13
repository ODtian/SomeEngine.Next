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
        using World world = new World(execution: new WorldJobs(scheduler));
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "joint-grid-100000");
        using var scene = PhysicsScaleScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60));

        Assert.Equal(100001, scene.Bodies.Length);
        Assert.Equal(298701, scene.Joints.Length);
        var anchor = scene.Bodies.Span[1];
        var firstHangingBody = scene.Bodies.Span[401];
        Assert.Equal(BodyMotion.Static, world.ReadComponentData<BodyMass>(anchor).Motion);
        Assert.Equal(BodyMotion.Dynamic, world.ReadComponentData<BodyMass>(firstHangingBody).Motion);
        Assert.Equal(new Vector3(.15f), world.ReadComponentData<BoxGeometry>(firstHangingBody).HalfExtents);
        var vertical = scene.Joints.Span[399];
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(vertical);
        Assert.Equal(anchor, endpoints.Source);
        Assert.Equal(firstHangingBody, endpoints.Target);
        JointConstraint joint = world.ReadComponentData<JointConstraint>(vertical);
        Assert.Equal(new Vector3(0, 0, -.6f), joint.SourceFrame.Position);
        Assert.Equal(new Vector3(0, 0, .6f), joint.TargetFrame.Position);
        var diagonal = world.ReadComponentData<RelationEndpoints<JointConstraint>>(scene.Joints.Span[^1]);
        Assert.Equal(scene.Bodies.Span[99599], diagonal.Source);
        Assert.Equal(scene.Bodies.Span[100000], diagonal.Target);
        { using var impulses = world.ReadBuffer<BodyImpulse>(scene.Bodies.Span[99801]); 
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(0, 12, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
         }
    }
}
