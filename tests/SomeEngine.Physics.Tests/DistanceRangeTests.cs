using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class DistanceRangeTests
{
    [Fact]
    public void ARadialDistanceRangeResistsBothBoundsThroughTheCommonBodyEquation()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        var anchor = world.Create(new BodyPose { Rotation = Quaternion.Identity });
        var moving = world.Create(new BodyPose { Position = new(3, 4, 0), Rotation = Quaternion.Identity });
        world.Add(anchor, new BodyMass { Motion = BodyMotion.Static });
        world.Add(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        foreach (var body in new[] { anchor, moving })
        {
            world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState()); world.Add(body, new AvbdBodyEquation());
        }
        var range = Relations<DistanceRange>.CreateEdge(world, anchor, moving, new DistanceRange { Minimum = 1, Maximum = 2, Stiffness = 1 });
        world.Add(range.Entity, new AvbdDistanceRangeState { Penalty = 1000 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<AvbdBodyEquation>());
        Query ranges = world.Query(new QueryDescription().WithStored<DistanceRange>()
            .WithStored<RelationEndpoints<DistanceRange>>().WithStored<AvbdDistanceRangeState>());
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdConstraints<DistanceRange, AvbdDistanceRangeMath>(ranges),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 32, 4, false), 32, 0, 0, bodyIterations: 3);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);

        physics.Schedule(0, 0, 1).Complete();

        Assert.InRange(Vector3.Distance(new(2.1f, 2.8f, 0), world.ReadComponentData<BodyPose>(moving).Position), 0, 1e-4f);
        world.Replace(moving, new BodyPose { Position = new(.3f, .4f, 0), Rotation = Quaternion.Identity });
        world.Replace(moving, new BodyVelocity());

        physics.Schedule(1, 1, 1).Complete();

        Assert.InRange(Vector3.Distance(new(.45f, .6f, 0), world.ReadComponentData<BodyPose>(moving).Position), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(anchor).Position);
    }
}
