using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class GeometryReaderBoundsTests
{
    [Fact]
    public void SelectedCapsuleBoundsUseTheReferencedBodyPositions()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(jobs));
        var first = world.Create(new BodyPose { Rotation = Quaternion.Identity });
        var second = world.Create(new BodyPose { Position = new(0, 2, 0), Rotation = Quaternion.Identity });
        var capsule = world.Create(new DynamicCapsule(new(first, default), new(second, default), .5f), default(CollisionBounds));
        Query members = world.Query(new QueryDescription().With<DynamicCapsule>());
        var update = new UpdateGeometryBounds<CapsuleGeometry, DynamicCapsuleReader>(world, members, new(), 64);
        var context = new PhysicsStepContext(world, members, 0, 0, 1, 0, .1);

        update.Schedule(in context, default).Complete();

        CollisionBounds bounds = world.ReadComponentData<CollisionBounds>(capsule);
        Assert.Equal(new Vector3(-.5f), bounds.Min);
        Assert.Equal(new Vector3(.5f, 2.5f, .5f), bounds.Max);
    }

    [Fact]
    public void SelectedMotionBoundsIncludeReferencedStartPredictionAndRadius()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(jobs));
        var first = world.Create(new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 }, new BodyVelocity(),
            new BodyStepState { StartRotation = Quaternion.Identity, PredictedRotation = Quaternion.Identity,
                PredictedCenterOfMass = new(2, 0, 0) });
        var second = world.Create(new BodyPose { Position = new(3, 0, 0), Rotation = Quaternion.Identity },
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 }, new BodyVelocity(),
            new BodyStepState { StartCenterOfMass = new(2, 0, 0), StartRotation = Quaternion.Identity,
                PredictedCenterOfMass = new(4, 0, 0), PredictedRotation = Quaternion.Identity });
        var capsule = world.Create(new DynamicCapsule(new(first, default), new(second, default), .5f), default(CollisionBounds));
        Query members = world.Query(new QueryDescription().With<DynamicCapsule>());
        var update = new UpdatePredictedBounds<CapsuleGeometry, DynamicCapsuleReader>(world, members, new(), .25f);
        var context = new PhysicsStepContext(world, members, 0, 0, 1, 0, .1);

        update.Schedule(in context, default).Complete();

        CollisionBounds bounds = world.ReadComponentData<CollisionBounds>(capsule);
        Assert.Equal(new Vector3(-.75f), bounds.Min);
        Assert.Equal(new Vector3(4.75f, .75f, .75f), bounds.Max);
    }
}
