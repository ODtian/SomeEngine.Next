using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class FootSupportBehaviorTests
{
    [Fact]
    public void PredictedContactsFollowTheQueriedSupportPointNormalAndMovingTransform()
    {
        var skeleton = new Skeleton([-1, 0, 0],
        [
            new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
            new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
            new() { Translation = new(0, 0, .4f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        ], [1, 2, 3]);
        var clip = new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new RawClipData(1, [])));
        FootMotion motion = FootMotion.Analyze(clip, 0, [new(1, 2)], new(4),
            [new(0, 0, .5f, FootContactKind.Free), new(0, .5f, 1, FootContactKind.ToePivot)]);
        var prediction = new FootPrediction(new(PosePath.FromClip(clip, motion), new(10, 0)), 1);
        Assert.True(prediction.TryGetNextContact(0, 10, 10, out FootPredictionSample contact));

        using var world = new World();
        var ground = world.CreateEntity(new CollisionSurface(Quad(
            new(-4, 0, -2), new(4, 0, -2), new(4, 0, 2), new(-4, 0, 2)), Matrix4x4.Identity, 11));
        var slope = world.CreateEntity(new CollisionSurface(Quad(
            new(-1, 0, -1), new(1, 1, -1), new(1, 1, 1), new(-1, 0, 1)), Matrix4x4.Identity, 12));
        CollisionMesh stepMesh = Quad(new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1));
        var step = world.CreateEntity(new CollisionSurface(stepMesh, Matrix4x4.CreateTranslation(3, .75f, 0), 13));
        var members = world.Query(world.QueryDefinition().All<CollisionSurface>());
        CollisionTree tree = Collision.Build<CollisionSurface, CollisionSurfaceReader>(world, members, default);
        using var scene = Collision.Query(world, members, tree);
        var feet = new FootSupportQueries(scene, new(Vector3.UnitY, 2, 2), capacity: 3);
        FootSupportRequest[] requests =
        [
            FootSupportRequest.FromPrediction(contact, Matrix4x4.CreateTranslation(-3, 0, -.4f)),
            FootSupportRequest.FromPrediction(contact, Matrix4x4.CreateTranslation(0, 0, -.4f)),
            FootSupportRequest.FromPrediction(contact, Matrix4x4.CreateTranslation(3.5f, 0, -.4f)),
        ];
        var targets = new FootContactTarget[3];
        feet.Sample(requests, targets);

        Assert.All(targets, target =>
        {
            Assert.True(target.HasSupport);
            Assert.Equal(10.5, target.Time);
            Assert.Equal(FootContactKind.ToePivot, target.Kind);
        });
        Assert.Equal(ground, targets[0].Support.Entity);
        Near(new(-3, 0, 0), targets[0].Support.Point);
        Near(Vector3.UnitY, targets[0].Support.Normal);
        Assert.Equal(slope, targets[1].Support.Entity);
        Near(new(0, .5f, 0), targets[1].Support.Point);
        Near(new(-.4472136f, .8944272f, 0), targets[1].Support.Normal);
        Assert.Equal(step, targets[2].Support.Entity);
        Near(new(3.5f, .75f, 0), targets[2].Support.Point);
        Near(new(.5f, 0, 0), targets[2].LocalPoint);
        Near(Vector3.UnitY, targets[2].LocalNormal);

        Matrix4x4 moved = Matrix4x4.CreateRotationZ(MathF.PI / 6) * Matrix4x4.CreateTranslation(4, 1, 0);
        world.Replace(step, new CollisionSurface(stepMesh, moved, 14));
        Assert.True(feet.TryRefresh(targets[2], out FootContactTarget current));
        Near(new(4.4330127f, 1.25f, 0), current.Support.Point);
        Near(new(-.5f, .8660254f, 0), current.Support.Normal);
        Assert.Equal(14UL, current.Support.SurfaceVersion);
        Assert.Equal(10.5, current.Time);
        Assert.Equal(FootContactKind.ToePivot, current.Kind);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void StrideEnvelopeIncludesRaisedGeometryBetweenItsFlatDepartureAndLanding()
    {
        using var world = new World();
        var ground = world.CreateEntity(new CollisionSurface(Quad(
            new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2)), Matrix4x4.Identity, 31));
        var step = world.CreateEntity(new CollisionSurface(Quad(
            new(.08f, .8f, -.5f), new(.12f, .8f, -.5f), new(.12f, .8f, .5f), new(.08f, .8f, .5f)), Matrix4x4.Identity, 32));
        var members = world.Query(world.QueryDefinition().All<CollisionSurface>());
        CollisionTree tree = Collision.Build<CollisionSurface, CollisionSurfaceReader>(world, members, default);
        using var scene = Collision.Query(world, members, tree);
        var feet = new FootSupportQueries(scene, new(Vector3.UnitY, 2, 2), capacity: 5);
        var samples = new FootTerrainSample[5];

        feet.SampleEnvelope(new(new(-.8f, 0, 0), new(.8f, 0, 0), .15f), samples);

        Assert.All(samples, sample => Assert.True(sample.HasSupport));
        Assert.Equal(0, samples[0].Fraction);
        Assert.Equal(ground, samples[0].Support.Entity);
        Near(new(-.8f, .15f, 0), samples[0].Center);
        Assert.Equal(1, samples[4].Fraction);
        Assert.Equal(ground, samples[4].Support.Entity);
        Near(new(.8f, .15f, 0), samples[4].Center);
        Assert.Equal(.5f, samples[2].Fraction);
        Assert.Equal(step, samples[2].Support.Entity);
        Near(new(0, .9268858f, 0), samples[2].Center);
        Near(new(.08f, .8f, 0), samples[2].Support.Point);
        Near(new(-.5333333f, .8459052f, 0), samples[2].Support.Normal);
        Assert.Equal(32UL, samples[2].Support.SurfaceVersion);
        world.ReleaseQuery(members);
    }

    private static CollisionMesh Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        => new(new Vector3[] { a, b, c, d }, new uint[] { 0, 2, 1, 0, 3, 2 });

    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, .00001f);
}
