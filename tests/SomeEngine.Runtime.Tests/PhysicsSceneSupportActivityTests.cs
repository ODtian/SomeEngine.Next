using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneSupportActivityTests
{
    [Fact]
    public void TheOriginalSpatialPyramidKeepsItsSleepingContactForcesWhileAnotherBodyFalls()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        var description = PhysicsScenes.All.Single(scene => scene.Id == "3d-resting-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        var forces = new Dictionary<PointIdentity, Vector3>();
        Entity falling = world.Clone(scene.Bodies.Span[1]);
        world.Set(falling, new BodyPose { Position = new(100, 0, 100), Rotation = Quaternion.Identity });
        for (int frame = 0; frame < 31; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();
            if (frame == 29)
            {
                forces = ReadPointForces(scene.Contacts!);
            }
        }

        Assert.Contains(forces.Values, force => force.LengthSquared() > 1);
        Assert.True(world.ReadComponentData<BodyPose>(falling).Position.Z < 100);
        Dictionary<PointIdentity, Vector3> after = ReadPointForces(scene.Contacts!);
        Assert.Equal(forces.Count, after.Count);
        foreach ((PointIdentity point, Vector3 value) in after) Assert.Equal(forces[point], value);

    }

    [Fact]
    public void TheOriginalRestingSpatialPyramidStaysAsleepAfterItsSupportLoadsSettle()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        var description = PhysicsScenes.All.Single(scene => scene.Id == "3d-resting-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        for (int frame = 0; frame < 60; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();
            if (frame >= 29)
                foreach (var body in scene.Bodies.Span[1..])
                    Assert.True(world.ReadComponentData<BodyActivity>(body).Sleeping);
        }
    }
    private readonly record struct PointIdentity(Entity First, Entity Second, uint FirstShape, uint SecondShape,
        int FirstFeature, int SecondFeature, int PointIndex);
    private static Dictionary<PointIdentity, Vector3> ReadPointForces(PhysicsContacts contacts)
    {
        ContactView view = contacts.OpenView();
        var values = new Dictionary<PointIdentity, Vector3>();
        for (int index = 0; index < view.PointCount; index++)
        {
            ContactPoint point = view.GetPoint(index); ContactPair pair = view.Pairs[point.PairIndex];
            values.Add(new(pair.FirstGeometry, pair.SecondGeometry, pair.FirstShape, pair.SecondShape,
                point.FirstFeature, point.SecondFeature, point.PointIndex), view.Responses[index].ForceOnSecond);
        }
        return values;
    }

}
