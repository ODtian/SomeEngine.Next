using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class MotionWarpingBehaviorTests
{
    [Fact]
    public void AppliedWarpedRootMotionReachesTheTargetAndMatchesTheContinuousPredictedPath()
    {
        using var world = new World();
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var raw = new RawClipData(2,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 2],
                [new(0, 0, 0, 0), new(4, 0, 0, 0)]),
            new(0, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 2],
                [new(0, 0, 0, 1), new(0, 0, 1, 0)]),
            new(1, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 2],
                [new(0, 1, 0, 0), new(0, 3, 0, 0)]),
        ]);
        var clip = new AnimationClip(skeleton, 2, ClipSampler.Create<RawClipData, RawClipSampler>(raw), rootMotionBone: 0);
        var path = RootMotionPath.FromClip(clip, sampleRate: 4);
        var warp = new RootMotionWarp(path, new MotionWarpWindow(.5f, 1.5f));
        Matrix4x4 start = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 0);
        Matrix4x4 destination = Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(13, 25, 0);
        MotionWarpInput input = warp.Bind(start, destination);
        float[] times = [.25f, .5f, 1, 1.5f, 2];
        Vector3[] positions = [new(10, 20.5f, 0), new(10, 21, 0), new(11.204707f, 23.2871f, 0),
            new(13, 25, 0), new(13.707107f, 25.707107f, 0)];
        float[] rotations = [5 * MathF.PI / 8, 3 * MathF.PI / 4, 7 * MathF.PI / 8, MathF.PI, 5 * MathF.PI / 4];
        Matrix4x4[] predictions = times.Select(input.SampleWorld).ToArray();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new PoseResultId(1));
        world.Add(target, new WarpingActorTransform { Transform = start });
        PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.WarpMotion(target, new MotionWarpParameters(warp, new PoseResultId(0), new PoseResultId(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new PoseResultId(0), RootMotion: RootMotionMode.Extract));
        }
        Entity animation = builder.Play();
        world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInput>(animation, ref input,
            static (instances, ref input) => instances[0].Input = input);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new MotionWarpSystem(), AnimationPhase.Composition));

        float previous = 0;
        for (int sample = 0; sample < times.Length; sample++)
        {
            var context = new ImmediateSystemContext(world, 0, 0, times[sample] - previous);
            systems.Update(ref context);
            PoseResult result = PoseAnimation.ReadResult(world, in output);
            WarpingActorTransform actor = world.Read<WarpingActorTransform>(target);
            actor.Transform = result.RootMotion.Transform * actor.Transform;
            world.Replace(target, actor);
            Matrix4x4 expected = Matrix4x4.CreateRotationZ(rotations[sample]) * Matrix4x4.CreateTranslation(positions[sample]);
            Near(expected, world.Read<WarpingActorTransform>(target).Transform);
            Near(predictions[sample], actor.Transform);
            Near(Matrix4x4.CreateTranslation(0, 1 + times[sample], 0), instance.ModelPose.Transforms[1]);
            Assert.Equal(times[sample], result.Time);
            Assert.Equal(times[sample] / 2, result.Phase);
            previous = times[sample];
        }
        Assert.Equal(1u, world.Read<Timeline>(animation).Completions);
        Matrix4x4 entrance = Matrix4x4.CreateRotationZ(3 * MathF.PI / 4) * Matrix4x4.CreateTranslation(10, 21, 0);
        Near(entrance, input.SampleWorld(.5f - .0001f), .001f);
        Near(entrance, input.SampleWorld(.5f + .0001f), .001f);
        Near(destination, input.SampleWorld(1.5f - .0001f), .001f);
        Near(destination, input.SampleWorld(1.5f + .0001f), .001f);
    }

    [Fact]
    public void SharedGraphKeepsEachCharactersTargetAndRestartedMotionIndependent()
    {
        using var world = new World();
        var skeleton = new Skeleton([-1], [new() { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1]);
        var raw = new RawClipData(1,
            [new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1], [new(0, 0, 0, 0), new(2, 0, 0, 0)])]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw), rootMotionBone: 0);
        var warp = new RootMotionWarp(RootMotionPath.FromClip(clip, 4), new(0, 1));
        MotionWarpInput[] inputs =
        [
            warp.Bind(Matrix4x4.Identity, Matrix4x4.CreateTranslation(4, 0, 0)),
            warp.Bind(Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0),
                Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(12, 3, 0)),
        ];
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        Entity[] targets =
        [
            PoseAnimation.CreateTarget(world, first, resultCount: 2, output: new PoseResultId(1)),
            PoseAnimation.CreateTarget(world, second, resultCount: 2, output: new PoseResultId(1)),
        ];
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new PoseResultId(0), RootMotion: RootMotionMode.Extract));
            builder.WarpMotion(Entity.Null, new(warp, new PoseResultId(0), new PoseResultId(1)));
        }
        using CompiledGraph graph = builder.Compile();
        Entity[] animations = [graph.Play(world, targets[0]), graph.Play(world, targets[1])];
        PoseResultReference[] outputs =
        [
            PoseAnimation.Resolve(world, targets[0], new PoseResultId(1)),
            PoseAnimation.Resolve(world, targets[1], new PoseResultId(1)),
        ];
        for (int actor = 0; actor < targets.Length; actor++)
        {
            world.Add(targets[actor], new WarpingActorTransform { Transform = inputs[actor].Start });
            world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInput>(animations[actor], ref inputs[actor],
                static (instances, ref input) => instances[0].Input = input);
        }
        PropertyAnimation.SetSpeed(world, animations[1], .5f);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new MotionWarpSystem(), AnimationPhase.Composition));
        var context = new ImmediateSystemContext(world, 0, 0, .5);
        for (int frame = 0; frame < 4; frame++)
        {
            systems.Update(ref context);
            for (int actor = 0; actor < targets.Length; actor++)
            {
                PoseResult result = PoseAnimation.ReadResult(world, in outputs[actor]);
                WarpingActorTransform transform = world.Read<WarpingActorTransform>(targets[actor]);
                transform.Transform = result.RootMotion.Transform * transform.Transform;
                world.Replace(targets[actor], transform);
                Near(inputs[actor].SampleWorld((float)result.Time), transform.Transform);
            }
        }
        Near(inputs[0].Target, world.Read<WarpingActorTransform>(targets[0]).Transform);
        Near(inputs[1].Target, world.Read<WarpingActorTransform>(targets[1]).Transform);

        animations[0] = graph.Play(world, targets[0]);
        world.Replace(targets[0], new WarpingActorTransform { Transform = inputs[0].Start });
        world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInput>(animations[0], ref inputs[0],
            static (instances, ref input) => instances[0].Input = input);
        systems.Update(ref context);
        PoseResult restarted = PoseAnimation.ReadResult(world, in outputs[0]);
        WarpingActorTransform restartedActor = world.Read<WarpingActorTransform>(targets[0]);
        restartedActor.Transform = restarted.RootMotion.Transform * restartedActor.Transform;
        world.Replace(targets[0], restartedActor);
        Near(Matrix4x4.CreateTranslation(2, 0, 0), world.Read<WarpingActorTransform>(targets[0]).Transform);
        Near(inputs[1].Target, world.Read<WarpingActorTransform>(targets[1]).Transform);
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual, float tolerance = .0001f)
    {
        foreach (Vector3 point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.InRange(Vector3.Distance(Vector3.Transform(point, expected), Vector3.Transform(point, actual)), 0, tolerance);
    }
}

public struct WarpingActorTransform : IComponent
{
    public Matrix4x4 Transform;
}
