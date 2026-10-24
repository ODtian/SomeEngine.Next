using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class SceneSurfaceQueryTests
{
    [Fact]
    public void SceneQueriesReturnNearestSupportOnFlatSlopeAndStepGeometry()
    {
        using var world = new World();
        var ground = world.CreateEntity(new CollisionSurface(Quad(
            new(-4, 0, -2), new(4, 0, -2), new(4, 0, 2), new(-4, 0, 2)), Matrix4x4.Identity, 11));
        var slope = world.CreateEntity(new CollisionSurface(Quad(
            new(-1, 0, -1), new(1, 1, -1), new(1, 1, 1), new(-1, 0, 1)), Matrix4x4.Identity, 12));
        var step = world.CreateEntity(new CollisionSurface(Quad(
            new(2, 0.75f, -1), new(4, 0.75f, -1), new(4, 0.75f, 1), new(2, 0.75f, 1)), Matrix4x4.Identity, 13));
        var members = world.Query(world.QueryDefinition().All<CollisionSurface>());
        CollisionTree tree = Collision.Build<CollisionSurface, CollisionSurfaceReader>(world, members, default);
        using var surfaces = Collision.Query(world, members, tree);

        CollisionRay[] rays = [new(new(-3, 2, 0), -Vector3.UnitY, 3),
            new(new(0, 2, 0), -Vector3.UnitY, 3), new(new(3, 2, 0), -Vector3.UnitY, 3)];
        var hits = new CollisionHit[3];
        var found = new bool[3];
        surfaces.Raycast(rays, hits, found);
        Assert.All(found, Assert.True);
        CollisionHit flatHit = hits[0], slopeHit = hits[1], stepHit = hits[2];
        Assert.Equal(ground, flatHit.Entity);
        Near(new(-3, 0, 0), flatHit.Point);
        Near(Vector3.UnitY, flatHit.Normal);
        Assert.Equal(2, flatHit.Distance);
        Assert.Equal(11UL, flatHit.SurfaceVersion);

        Assert.Equal(slope, slopeHit.Entity);
        Near(new(0, 0.5f, 0), slopeHit.Point);
        Near(new(-0.4472136f, 0.8944272f, 0), slopeHit.Normal);
        Assert.Equal(1.5f, slopeHit.Distance);
        Assert.Equal(12UL, slopeHit.SurfaceVersion);

        Assert.Equal(step, stepHit.Entity);
        Near(new(3, 0.75f, 0), stepHit.Point);
        Near(Vector3.UnitY, stepHit.Normal);
        Assert.Equal(1.25f, stepHit.Distance);
        Assert.Equal(13UL, stepHit.SurfaceVersion);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void MovingSurfaceUpdatesKeepTheContactAnchorAndSphereSweepOnItsCurrentTransform()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        CollisionMesh mesh = Quad(new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1));
        var reader = new PlatformReader(mesh);
        var platform = world.CreateEntity(new Platform(Matrix4x4.CreateTranslation(0, 0.5f, 0), 21));
        var members = world.Query(world.QueryDefinition().All<Platform>());
        CollisionTree tree = Collision.Build<Platform, PlatformReader>(world, members, reader);
        using var maintenance = Collision.Candidates<Platform, PlatformReader>(world, members, tree, reader)
            .CreateMaintenance(jobs);
        maintenance.Schedule().Complete();
        using var surfaces = Collision.Query<Platform, PlatformReader>(world, members, tree, reader);

        Assert.True(surfaces.SweepSphere(new(new(0.5f, 2, 0), 0.2f, -Vector3.UnitY, 3), out CollisionHit first));
        Assert.Equal(platform, first.Entity);
        Near(new(0.5f, 0.5f, 0), first.Point);
        Near(Vector3.UnitY, first.Normal);
        Near(1.3f, first.Distance);
        Matrix4x4.Invert(first.Transform, out Matrix4x4 inverse);
        Vector3 localAnchor = Vector3.Transform(first.Point, inverse);

        Matrix4x4 moved = Matrix4x4.CreateRotationZ(MathF.PI / 6) * Matrix4x4.CreateTranslation(1, 1, 0);
        world.AcquireSystemVersion();
        world.Replace(platform, new Platform(moved, 22));
        maintenance.Schedule().Complete();
        Assert.True(surfaces.TryGetSurface(platform, out CollisionSurface current));
        Near(new(1.4330127f, 1.25f, 0), Vector3.Transform(localAnchor, current.Transform));
        Assert.Equal(22UL, current.Version);

        Assert.True(surfaces.SweepSphere(new(new(1.4330127f, 3, 0), 0.2f, -Vector3.UnitY, 3), out CollisionHit second));
        Assert.Equal(platform, second.Entity);
        Near(new(1.5330127f, 1.307735f, 0), second.Point);
        Near(new(-0.5f, 0.8660254f, 0), second.Normal);
        Near(1.5190599f, second.Distance);
        Assert.Equal(22UL, second.SurfaceVersion);
        Assert.Equal(moved, second.Transform);
        world.ReleaseQuery(members);
    }

    private readonly record struct Platform(Matrix4x4 Transform, ulong Version) : IComponent;

    private readonly struct PlatformReader(CollisionMesh mesh) : ICollisionSurfaceReader<Platform>
    {
        public CollisionBounds Read(in Platform component) => ReadSurface(in component).Bounds;
        public CollisionSurface ReadSurface(in Platform component) => new(mesh, component.Transform, component.Version);
    }

    private static CollisionMesh Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        => new(new Vector3[] { a, b, c, d }, new uint[] { 0, 2, 1, 0, 3, 2 });

    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private static void Near(float expected, float actual)
        => Assert.InRange(MathF.Abs(expected - actual), 0, 0.00001f);
}
