using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PrimitiveGeometryQueryTests
{
    [Fact]
    public void ComponentQuerySelectsItsGeometryFromAMixedCollisionTree()
    {
        using var world = new World();
        Entity sphere = AddGeometry(world, new SphereGeometry(1), new BodyPose { Rotation = Quaternion.Identity });
        AddGeometry(world, new BoxGeometry(new Vector3(.5f)),
            new BodyPose { Position = new Vector3(3, 0, 0), Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Any<SphereGeometry>().Any<BoxGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<SphereGeometry>(world, members, tree, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(4, 0, 0), -Vector3.UnitX, 6), out CollisionHit hit));

        Assert.Equal(sphere, hit.Entity);
        Near(Vector3.UnitX, hit.Point);
        Near(3, hit.Distance);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void CylinderRaysAndSphereSweepsMeetTheSameAnalyticSurface()
    {
        using var world = new World();
        var geometry = new CylinderGeometry(1, 1);
        var pose = new BodyPose { Rotation = Quaternion.Identity };
        Entity cylinder = AddGeometry(world, geometry, pose);
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<CylinderGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<CylinderGeometry>(world, members, tree, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(3, 0, 0), -Vector3.UnitX, 5), out CollisionHit ray));
        Assert.True(queries.SweepSphere(new(new Vector3(3, 0, 0), .25f, -Vector3.UnitX, 5), out CollisionHit sphere));

        Assert.Equal(cylinder, ray.Entity);
        Assert.Equal(cylinder, sphere.Entity);
        Near(Vector3.UnitX, ray.Point);
        Near(Vector3.UnitX, sphere.Point);
        Near(Vector3.UnitX, ray.Normal);
        Near(Vector3.UnitX, sphere.Normal);
        Near(2, ray.Distance);
        Near(1.75f, sphere.Distance);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void GeometryDistanceFindsTheNearestShapeAndItsWitnesses()
    {
        using var world = new World();
        var box = new BoxGeometry(Vector3.One);
        Entity near = AddGeometry(world, box, new BodyPose { Rotation = Quaternion.Identity });
        AddGeometry(world, box, new BodyPose { Position = new Vector3(8, 0, 0), Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<BoxGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<BoxGeometry>(world, members, tree, new(1e-5f, 64));
        var sphere = new SphereGeometry(.5f);
        var pose = new BodyPose { Position = new Vector3(3, 0, 0), Rotation = Quaternion.Identity };

        Assert.True(queries.Distance(in sphere, in pose, new SphereCollision<BoxGeometry>(), out CollisionDistanceHit hit));

        Assert.Equal(near, hit.Entity);
        Near(1.5f, hit.Distance.Separation);
        Near(-Vector3.UnitX, hit.Distance.Normal);
        Near(new Vector3(2.5f, 0, 0), hit.Distance.FirstPoint);
        Near(Vector3.UnitX, hit.Distance.SecondPoint);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void GeometryOverlapReturnsOnlyTheIntersectingSceneEntity()
    {
        using var world = new World();
        var box = new BoxGeometry(Vector3.One);
        Entity near = AddGeometry(world, box, new BodyPose { Rotation = Quaternion.Identity });
        AddGeometry(world, box, new BodyPose { Position = new Vector3(5, 0, 0), Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<BoxGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<BoxGeometry>(world, members, tree, new(1e-5f, 64));
        var sphere = new SphereGeometry(.5f);
        var pose = new BodyPose { Position = new Vector3(1.25f, 0, 0), Rotation = Quaternion.Identity };
        Span<Entity> matches = stackalloc Entity[2];

        int count = queries.Overlap(in sphere, in pose, new SphereCollision<BoxGeometry>(), matches);

        Assert.Equal(1, count);
        Assert.Equal(near, matches[0]);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void BoxSweepReturnsTheFirstSceneContact()
    {
        using var world = new World();
        var target = new BoxGeometry(Vector3.One);
        Entity box = AddGeometry(world, target, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<BoxGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<BoxGeometry>(world, members, tree, new(1e-5f, 64));
        var moving = new BoxGeometry(new Vector3(.5f));
        var pose = new BodyPose { Position = new Vector3(3, 0, 0), Rotation = Quaternion.Identity };
        var sweep = new ConvexShapeSweep<BoxGeometry, BoxGeometry, ConvexCollision<BoxGeometry, BoxGeometry>>(
            new(new(1e-5f, 32, 64)), new(1e-5f, 64));

        Assert.True(queries.Sweep(in moving, in pose, -Vector3.UnitX, 5, sweep, out CollisionHit hit));

        Assert.Equal(box, hit.Entity);
        Near(1.5f, hit.Distance);
        Near(1, hit.Point.X);
        Near(Vector3.UnitX, hit.Normal);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void PhysicsMotionUpdatesTheMaintainedQueryBoundsAndSupportTransform()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var sphere = new SphereGeometry(1);
        Entity body = AddGeometry(world, sphere, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity { Linear = new Vector3(3, 0, 0) });
        world.Add(body, new BodyStepState());
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var maintenance = Collision.Candidates(world, members, tree).CreateMaintenance(scheduler);
        maintenance.Schedule().Complete();
        var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
            .Then(new UpdateGeometryBounds<SphereGeometry>(rowsPerPacket: 64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);

        PhysicsStepResult step = physics.Schedule(number: 0, startTime: 0, duration: 1);
        maintenance.Schedule(step.Completion).Complete();
        using var queries = Collision.Query<SphereGeometry>(world, members, tree, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(6, 0, 0), -Vector3.UnitX, 3), out CollisionHit hit));
        Assert.Equal(body, hit.Entity);
        Near(new Vector3(4, 0, 0), hit.Point);
        Near(2, hit.Distance);
        Assert.True(queries.TryGetTransform(body, out Matrix4x4 transform, out _));
        Near(new Vector3(3, 0, 0), transform.Translation);
        world.ReleaseQuery(members);
    }

    private static Entity AddGeometry<TGeometry>(World world, TGeometry geometry, BodyPose pose)
        where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    {
        Entity entity = world.CreateEntity(geometry);
        world.Add(entity, pose);
        world.Add(entity, TGeometry.Bounds(in geometry, in pose));
        return entity;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    private static void Near(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0, 1e-4f);
}
