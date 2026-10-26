using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class ConstraintParticipantTests
{
    [Fact]
    public void RetargetingABendParticipantMovesTheNewBodyAndDestroyingTheBendReleasesItsEdges()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, Vector3.Zero);
        Entity oldMiddle = CreateBody(world, Vector3.UnitX);
        Entity middle = CreateBody(world, Vector3.UnitX);
        Entity last = CreateBody(world, new Vector3(1, 1, 0));
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f }, [first, oldMiddle, last]);
        RelationEdge<ConstraintParticipant> participant = default;
        world.ExecuteBufferRead<ConstraintBodyLink>(bend, links => participant = links[1].Participant);
        world.RetargetRelationImmediate(participant, bend, middle);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle bends = world.Query(ConstraintBodies.AddReads(new QueryDefinitionBuilder().ReadWrite<XpbdBend>().Build()));
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdBend, XpbdBendProjection>(bends),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        var stages = new PredictBodies(Vector3.Zero, 64).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1).Complete();

        Assert.Equal(new Vector3(.875f, .125f, 0), world.Read<BodyPose>(middle).Position);
        Assert.Equal(Vector3.UnitX, world.Read<BodyPose>(oldMiddle).Position);
        Assert.Equal(new Vector3(0, -.125f, 0), world.Read<BodyPose>(first).Position);
        Assert.Equal(new Vector3(1.125f, 1, 0), world.Read<BodyPose>(last).Position);
        world.DestroyEntity(bend);
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(first).Count);
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(middle).Count);
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(last).Count);
        world.ReleaseQuery(bends);
        world.ReleaseQuery(bodies);
    }

    private static Entity CreateBody(World world, Vector3 position)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        return body;
    }
}
