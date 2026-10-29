using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PlanarCollisionTests
{
    [Fact]
    public void SeparatedParallelFacesProvideBothPlanarContactPoints()
    {
        var floor = new BoxGeometry(new(5, .5f, .01f));
        var body = new BoxGeometry(new(1, 1, .01f));
        var floorPose = new BodyPose { Position = new(0, -.5f, 0), Rotation = Quaternion.Identity };
        var bodyPose = new BodyPose { Position = new(0, 1.02f, 0), Rotation = Quaternion.Identity };
        Span<CollisionDistance> points = stackalloc CollisionDistance[2];

        int count = new PlanarBoxCollision(1e-5f).GetManifold(in floor, in floorPose, in body, in bodyPose, .03f, points);

        Assert.Equal(2, count);
        Assert.Equal(-1, MathF.Min(points[0].FirstPoint.X, points[1].FirstPoint.X));
        Assert.Equal(1, MathF.Max(points[0].FirstPoint.X, points[1].FirstPoint.X));
        foreach (CollisionDistance point in points[..count])
        {
            Assert.Equal(Vector3.UnitY, point.Normal);
            Assert.InRange(point.Separation, .01999f, .02001f);
        }
    }

    [Fact]
    public void TwoInPlaneContactPointsSupportTheThinBoxThroughTheCommonPhysicsStep()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity floor = CreateBody(world, new Vector3(0, -.5f, 0), new BoxGeometry(new(5, .5f, .01f)),
            new BodyMass { Motion = BodyMotion.Static });
        Entity body = CreateBody(world, new Vector3(0, .75f, 0), new BoxGeometry(new(1, 1, .01f)),
            new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = new Matrix4x4 { M33 = 1.5f } });
        QueryHandle shapes = world.Query(new QueryDefinitionBuilder().All<BoxGeometry>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle scope = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>()
            .ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read)
            .ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        using var output = new ContactPointBuffer(scheduler, 2, 4);
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<BoxGeometry, BoxGeometry>.Detect(
            new PlanarBoxCollision(1e-5f), new ContactPointOutput(output), 0));
        using var detection = DetectContactPoints.Create(world, world.Within(shapes).PartitionPairs(), bins,
            new BoxPairs(), narrow, output, ReadOnlySpan<JobResourceAccess>.Empty);
        using var points = ContactPoints.Create(world, poses, scope, output, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1e6f }), new ContactRefreshSettings(.01f, .99f, true));
        using var solve = SolveAvbdGraph.Create(world, bodies, new AvbdGraphConstraints<ContactPoint, AvbdContactMath>(contacts),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, 1, 1, 16, 16, false), 32, 0, 0);
        var stages = new PredictBodies(new Vector3(0, -10, 0), 64).Then(detection).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, bodies, contacts)).Then(solve).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        BodyPose pose = world.Read<BodyPose>(body);
        Assert.InRange(pose.Position.Y, .99998f, 1.00001f);
        Assert.Equal(0, pose.Position.Z); Assert.Equal(0, pose.Rotation.X); Assert.Equal(0, pose.Rotation.Y);
        var normals = new List<Vector3>();
        world.ExecuteReadQuery(scope, rows => { foreach (QueryRow row in rows.Rows) normals.Add(row.ReadRef<ContactPoint>().Normal); });
        Assert.Equal(2, normals.Count); Assert.All(normals, normal => Assert.Equal(Vector3.UnitY, normal));
        Assert.Equal(new Vector3(0, -.5f, 0), world.Read<BodyPose>(floor).Position);
        world.ReleaseQuery(contacts); world.ReleaseQuery(scope); world.ReleaseQuery(bodies); world.ReleaseQuery(poses); world.ReleaseQuery(shapes);
    }

    private readonly struct BoxPairs : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 1);
    }
    private static Entity CreateBody(World world, Vector3 position, BoxGeometry geometry, BodyMass mass)
    {
        Entity entity = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(entity, mass); world.Add(entity, geometry); world.Add(entity, new BodyVelocity());
        world.Add(entity, new BodyStepState()); world.Add(entity, new AvbdBodyEquation());
        world.Add(entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        return entity;
    }
}
