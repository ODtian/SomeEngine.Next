using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PlanarBodyTests
{
    [Fact]
    public void AnOffCenterSpringMovesThePlanarBodyAndRotatesItAboutItsOriginalIzzAxis()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity anchor = CreateBody(world, Vector3.UnitX, new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, Vector3.Zero, new BodyMass
        {
            Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = new Matrix4x4 { M33 = 1 },
        });
        Entity spring = world.CreateRelation(anchor, body, new ElasticConnection
        {
            SecondAnchor = Vector3.UnitY, RestRotation = Quaternion.Identity, LinearStiffness = 1,
        }).Entity;
        world.Add(spring, new AvbdElasticState { LinearPenalty = 1e6f });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle springs = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>()
            .Read<UndirectedRelationEndpoints<ElasticConnection>>().ReadWrite<AvbdElasticState>());
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdGraphConstraints<ElasticConnection, AvbdElasticConnectionMath>(springs),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1, 0, 0);
        var stages = new ApplyGyroscopicTorque(64).Then(new PredictBodies(Vector3.Zero, 64))
            .Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        // Unit mass/inertia and the offset spring give the first Newton change (1/3, -1/2, -1/3 rad).
        BodyPose pose = world.Read<BodyPose>(body);
        Assert.InRange(pose.Position.X, .33332f, .33335f);
        Assert.InRange(pose.Position.Y, -.50001f, -.49999f);
        Assert.Equal(0, pose.Position.Z);
        Assert.Equal(0, pose.Rotation.X); Assert.Equal(0, pose.Rotation.Y);
        Assert.InRange(pose.Rotation.Z, -.16441f, -.16438f);
        Assert.InRange(world.Read<BodyVelocity>(body).Angular.Z, -.33031f, -.33028f);
        world.ReleaseQuery(springs); world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position, BodyMass mass)
    {
        Entity entity = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(entity, mass); world.Add(entity, new BodyVelocity()); world.Add(entity, new BodyStepState());
        world.Add(entity, new AvbdBodyEquation());
        return entity;
    }
}
