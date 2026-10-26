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
    public void AUserConstraintFollowsFiveProtectedParticipantsThroughRetargetingAndDestruction()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity[] nodes = Enumerable.Range(0, 5).Select(index => CreateBody(world, new Vector3(index, 1, 0))).ToArray();
        Entity constraint = ConstraintBodies.Create(world, new MeanHeight(0), nodes);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle constraints = world.Query(ConstraintBodies.AddReads(new QueryDefinitionBuilder().Read<MeanHeight>().Build()));
        using var maintenance = new MaintainConstraintBodies(world, constraints);
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<MeanHeight, MeanHeightProjection>(constraints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            maintenance.Then(new PredictBodies(Vector3.Zero, 64)).Then(solve).Then(new UpdateBodyVelocities(64)), 1);
        physics.Schedule(0, 0, 1).Complete();
        foreach (Entity node in nodes) { Assert.InRange(MathF.Abs(world.Read<BodyPose>(node).Position.Y), 0, 1e-6f); world.Replace(node, new BodyVelocity()); }
        Entity replacement = CreateBody(world, new Vector3(2, 5, 0));
        RelationEdge<ConstraintParticipant> participant = default;
        world.ExecuteBufferRead<ConstraintBodyLink>(constraint, links => participant = links[2].Participant);
        world.RetargetRelationImmediate(participant, constraint, replacement);

        physics.Schedule(1, 1, .25).Complete();

        Assert.InRange(world.Read<BodyPose>(replacement).Position.Y, 3.999999f, 4.000001f);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(nodes[2]).Position.Y), 0, 1e-6f);
        Assert.InRange(world.Read<BodyPose>(nodes[0]).Position.Y, -1.000001f, -.999999f);
        world.Replace(nodes[0], new BodyVelocity { Linear = Vector3.UnitY });
        world.DestroyEntity(replacement);
        physics.Schedule(2, 1.25, .25).Complete();
        Assert.False(world.IsAlive(constraint));
        Assert.InRange(world.Read<BodyPose>(nodes[0]).Position.Y, -.750001f, -.749999f);
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(nodes[0]).Count);
        world.ReleaseQuery(constraints); world.ReleaseQuery(bodies);
    }

    private readonly record struct MeanHeight(float Height) : IComponent;
    private readonly struct MeanHeightProjection : IXpbdConstraint<MeanHeight>, IPhysicsConstraintBodies<MeanHeight>
    {
        public static int GetBodyCount(in MeanHeight constraint, scoped QueryRow row, in PhysicsStepContext context) => row.ReadBuffer<ConstraintBodyLink>().Count;
        public static Entity GetBody(in MeanHeight constraint, scoped QueryRow row, int index, in PhysicsStepContext context) =>
            ConstraintBodies.GetBody(in row.ReadBuffer<ConstraintBodyLink>().AsSpan()[index], in context);
        public static int GetReadEntityCount(in MeanHeight constraint, scoped QueryRow row, in PhysicsStepContext context) => GetBodyCount(in constraint, row, in context);
        public static Entity GetReadEntity(in MeanHeight constraint, scoped QueryRow row, int index, in PhysicsStepContext context) =>
            row.ReadBuffer<ConstraintBodyLink>().AsSpan()[index].Participant.Entity;
        public static void Initialize(in MeanHeight constraint, scoped QueryRow row, in PhysicsStepContext context) { }
        public static void Project(in MeanHeight constraint, scoped QueryRow row, scoped PhysicsBodies bodies, in PhysicsStepContext context)
        {
            ReadOnlySpan<ConstraintBodyLink> links = row.ReadBuffer<ConstraintBodyLink>().AsSpan();
            float height = 0, weight = 0;
            foreach (ref readonly ConstraintBodyLink link in links)
            {
                Entity body = ConstraintBodies.GetBody(in link, bodies, in context);
                height += bodies.ReadPose(body).Position.Y / links.Length;
                weight += bodies.ReadMass(body).InverseMass / (links.Length * links.Length);
            }
            float change = (constraint.Height - height) / weight;
            foreach (ref readonly ConstraintBodyLink link in links)
            {
                Entity body = ConstraintBodies.GetBody(in link, bodies, in context);
                bodies.WritePose(body).Position.Y += bodies.ReadMass(body).InverseMass * change / links.Length;
            }
        }
    }

    [Fact]
    public void DestroyingAParticipatingBodyRemovesTheBendBeforeTheNextSolve()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = CreateBody(world, Vector3.Zero);
        Entity middle = CreateBody(world, Vector3.UnitX);
        Entity last = CreateBody(world, new Vector3(1, 1, 0));
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f }, [first, middle, last]);
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle bends = world.Query(ConstraintBodies.AddReads(new QueryDefinitionBuilder().ReadWrite<XpbdBend>().Build()));
        using var maintenance = new MaintainConstraintBodies(world, bends);
        using var solve = SolveXpbdGraph.Create(world, bodies, new XpbdGraphConstraints<XpbdBend, XpbdBendProjection>(bends),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 1);
        var stages = maintenance.Then(new PredictBodies(Vector3.Zero, 64)).Then(solve).Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, 1).Complete();
        Vector3 position = world.Read<BodyPose>(first).Position;
        world.Replace(first, new BodyVelocity { Linear = Vector3.UnitX });
        world.DestroyEntity(middle);

        physics.Schedule(1, 1, .1).Complete();

        Assert.False(world.IsAlive(bend));
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(first).Count);
        Assert.Equal(0, world.GetIncomingRelations<ConstraintParticipant>(last).Count);
        Assert.InRange(Vector3.Distance(position + Vector3.UnitX * .1f, world.Read<BodyPose>(first).Position), 0, 1e-6f);
        world.ReleaseQuery(bends); world.ReleaseQuery(bodies);
    }

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
