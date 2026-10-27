using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class SampleMotionBehaviorTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task SeededSampleMotionUsesAbsoluteSecondsAndKeepsTheAuthoredTransform(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"];
        var platform = new SceneTestPlatform(
            [new TestScene("Seeded sample motion", content.DirectoryPath, Animate: true)],
            RuntimeStartupOptions.Parse(args));
        ApplicationScene application = platform.Application;
        platform.SampleOptions = platform.SampleOptions with { SurfacePanel = false, HudOverlay = false, WorldNameplates = false };
        Entity target = Entity.Null;
        var baseline = new TransformQvvs(new(3, -2, 7), Quaternion.CreateFromYawPitchRoll(.4f, -.2f, .1f), .75f)
            { Stretch = new(2, 3, 4) };
        Action<World, Entity> loadMesh = Assert.IsType<Action<World, Entity>>(application.InitializeMesh);
        application.InitializeMesh = (world, entity) =>
        {
            if (target == Entity.Null)
            {
                target = entity;
                world.Replace(entity, new WorldTransform { Qvvs = baseline });
                var motion = new MeshMotion { Amplitude = new(2, -3, .5f), Seed = 73 };
                if (world.Has<MeshMotion>(entity)) world.Replace(entity, motion);
                else world.Add(entity, motion);
            }
            loadMesh(world, entity);
        };
        bool observed = false;
        application.Initialize += frame =>
        {
            Assert.NotEqual(Entity.Null, target);
            Assert.False(frame.World.Has<MeshMotion>(target));
            Action<SceneEdit, float> simulate = Assert.IsType<Action<SceneEdit, float>>(application.SimulateScene);
            // Independent float32 reference values for amplitude (2,-3,.5), seed 73.
            (float Seconds, Vector3 Position)[] expected =
            [
                (0, new(3, -2, 7)),
                (.25f, new(3.38825965f, -2.75172234f, 7.08546686f)),
                (1.5f, new(4.84323025f, -4.99607563f, 7.42882061f)),
                (7.25f, new(1.84209108f, -4.61961365f, 6.51800299f)),
                (.25f, new(3.38825965f, -2.75172234f, 7.08546686f)),
            ];
            foreach ((float seconds, Vector3 position) in expected)
            {
                simulate(frame.Scene, seconds);
                AssertTransform(frame.World.Read<WorldTransform>(target).Qvvs, position, baseline);
            }
            frame.World.Replace(target, new WorldTransform
            {
                Qvvs = new(new Vector3(100, 200, 300), Quaternion.Identity, 9) { Stretch = Vector3.One },
            });
            simulate(frame.Scene, .25f);
            AssertTransform(frame.World.Read<WorldTransform>(target).Qvvs,
                new(3.38825965f, -2.75172234f, 7.08546686f), baseline);
            observed = true;
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(observed);
        Assert.Equal(1, result.CompletedFrames);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ZeroInterfaceDeltaDoesNotAdvanceSampleMotion(string backend)
    {
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"];
        var platform = new SceneTestPlatform(
            [new TestScene("Sample motion and view time", content.DirectoryPath, Animate: true)],
            RuntimeStartupOptions.Parse(args));
        ApplicationScene application = platform.Application;
        platform.SampleOptions = platform.SampleOptions with { SurfacePanel = false, HudOverlay = false, WorldNameplates = false };
        Entity target = Entity.Null;
        var baseline = new TransformQvvs(new(3, -2, 7), Quaternion.CreateFromYawPitchRoll(.4f, -.2f, .1f), .75f)
            { Stretch = new(2, 3, 4) };
        Action<World, Entity> loadMesh = Assert.IsType<Action<World, Entity>>(application.InitializeMesh);
        application.InitializeMesh = (world, entity) =>
        {
            if (target == Entity.Null)
            {
                target = entity;
                world.Replace(entity, new WorldTransform { Qvvs = baseline });
                var motion = new MeshMotion { Amplitude = new(2, -3, .5f), Seed = 73 };
                if (world.Has<MeshMotion>(entity)) world.Replace(entity, motion);
                else world.Add(entity, motion);
            }
            loadMesh(world, entity);
        };
        bool observed = false;
        application.Initialize += frame =>
        {
            Assert.NotEqual(Entity.Null, target);
            Action<SceneEdit, float> simulate = Assert.IsType<Action<SceneEdit, float>>(application.SimulateScene);
            Vector3 expected = new(4.84323025f, -4.99607563f, 7.42882061f);
            simulate(frame.Scene, 1.5f);
            frame.Systems.UpdateViews(0);
            AssertTransform(frame.World.Read<WorldTransform>(target).Qvvs, expected, baseline);
            frame.Systems.UpdateViews(0);
            AssertTransform(frame.World.Read<WorldTransform>(target).Qvvs, expected, baseline);
            simulate(frame.Scene, 1.5f);
            AssertTransform(frame.World.Read<WorldTransform>(target).Qvvs, expected, baseline);
            observed = true;
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(observed);
        Assert.Equal(1, result.CompletedFrames);
    }

    private static void AssertTransform(TransformQvvs actual, Vector3 position, TransformQvvs baseline)
    {
        Assert.InRange(MathF.Abs(actual.Position.X - position.X), 0, .00001f);
        Assert.InRange(MathF.Abs(actual.Position.Y - position.Y), 0, .00001f);
        Assert.InRange(MathF.Abs(actual.Position.Z - position.Z), 0, .00001f);
        Assert.Equal(baseline.Rotation, actual.Rotation);
        Assert.Equal(baseline.Stretch, actual.Stretch);
        Assert.Equal(baseline.Scale, actual.Scale);
    }
}
