using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneSupportActivityTests
{
    [Fact]
    public void TheOriginalSpatialPyramidKeepsItsSolvedContactForcesWhileSleeping()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var description = PhysicsScenes.All.Single(scene => scene.Id == "3d-resting-pyramid");
        using var scene = PhysicsSceneExample.CreateCpu(description).CreateSimulation(world);
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactResponse>());
        var forces = new Dictionary<Entity, Vector3>();
        for (int frame = 0; frame < 31; frame++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();
            if (frame == 29)
                world.ExecuteReadQuery(points, rows =>
                {
                    foreach (QueryRow row in rows.Rows)
                        forces.Add(row.Entity, row.ReadRef<ContactResponse>().ForceOnSecond);
                });
        }

        Assert.Contains(forces.Values, force => force.LengthSquared() > 1);
        world.ExecuteReadQuery(points, rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                Assert.True(forces.TryGetValue(row.Entity, out Vector3 force));
                Assert.Equal(force, row.ReadRef<ContactResponse>().ForceOnSecond);
            }
        });
        world.ReleaseQuery(points);
    }

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
