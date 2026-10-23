using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PosePredictionBehaviorTests
{
    private static readonly Channel<float> OverrideWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);
    private static readonly Channel<float> AdditiveWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[2].Weight);
    private static readonly Channel<Vector3> RootPosition = Channel<Vector3>.Of<RootFollow>(
        static (ref RootFollow root) => ref root.Position);

    [Fact]
    public void SelectedSequenceMatchesIndependentPlaybackAndKeepsTheLiveCharacterAtItsCurrentTime()
    {
        var skeleton = new Skeleton([-1, 0, 1, 2, 0],
            [Bone(Vector3.Zero), Bone(new(0, 2, 0)), Bone(new(0, -1, 0)),
             Bone(new(0.5f, 0, 0)), Bone(new(0, 0, 3))], [0, 1, 2, 3, 4]);
        AnimationClip first = Clip(skeleton, 1, new(0, 2, 1), 11);
        AnimationClip second = Clip(skeleton, 2, new(3, 5, 2), 12);
        using var liveWorld = new World();
        using var referenceWorld = new World();
        var storage = new PoseStorage();
        using SkeletonInstance live = storage.CreateInstance(skeleton);
        using SkeletonInstance reference = storage.CreateInstance(skeleton);
        Entity liveTarget = PoseAnimation.CreateTarget(liveWorld, live);
        Entity referenceTarget = PoseAnimation.CreateTarget(referenceWorld, reference);
        Entity liveAnimation = PlaySequence(liveWorld, liveTarget, first, second);
        Entity referenceAnimation = PlaySequence(referenceWorld, referenceTarget, first, second);
        using var liveSystems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(liveWorld));
        using var referenceSystems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(referenceWorld));
        PoseAnimation.AddSystems(liveSystems);
        PoseAnimation.AddSystems(referenceSystems);
        Tick(liveSystems, liveWorld, 0.25);
        Timeline liveTime = liveWorld.Read<Timeline>(liveAnimation);
        Matrix4x4[] livePose = live.ModelPose.Transforms.ToArray();
        ulong liveVersion = live.ModelPose.Version;
        Assert.Equal(1u, EventCount(liveWorld, liveAnimation, 11));

        PosePath path = PosePath.Sequence([PosePath.FromClip(first), PosePath.FromClip(second)]);
        var request = new PosePredictionRequest(path, new PoseTimeOrigin(10, 0.25f), Conditions: 41);
        short[] bones = [0, 3, 2];
        var batch = new PosePredictionBatch([new(request, bones), new(request, bones)]);
        var poses = new Matrix4x4[batch.OutputCount];
        var samples = new PosePredictionSample[batch.Count];
        batch.SampleModel([10.5, 11.5], calculationTime: 10.125, poses, samples);

        Tick(referenceSystems, referenceWorld, 0.75);
        Match(reference, bones, poses.AsSpan(batch.GetOutputRange(0).Start, bones.Length));
        Tick(referenceSystems, referenceWorld, 1);
        Match(reference, bones, poses.AsSpan(batch.GetOutputRange(1).Start, bones.Length));
        Assert.Equal(request, samples[1].Request);
        Assert.Equal(11.5, samples[1].Time);
        Assert.Equal(1.75f, samples[1].PathTime);
        Assert.Equal(10.125, samples[1].CalculatedAt);

        PosePath changedPath = PosePath.FromClip(second);
        var changedRequest = new PosePredictionRequest(changedPath, new PoseTimeOrigin(10, 0.5f, 2), Conditions: 42);
        var changed = new PosePredictionBatch([new(changedRequest, bones)]);
        changed.SampleModel([10.5], calculationTime: 10.25, poses.AsSpan(0, bones.Length), samples.AsSpan(0, 1));
        PropertyAnimation.Stop(referenceWorld, referenceAnimation);
        using (var builder = new AnimationBuilder(referenceWorld))
        {
            builder.Leaf<ClipParameters, ClipLeaf>(referenceTarget, new(second, new(0)));
            builder.Play();
        }
        Tick(referenceSystems, referenceWorld, 1.5);
        Match(reference, bones, poses.AsSpan(0, bones.Length));
        Assert.Equal(changedRequest, samples[0].Request);
        Assert.Equal(1.5f, samples[0].PathTime);
        Assert.Equal(10.25, samples[0].CalculatedAt);
        Assert.Equal(liveTime, liveWorld.Read<Timeline>(liveAnimation));
        Assert.Equal(livePose, live.ModelPose.Transforms.ToArray());
        Assert.Equal(liveVersion, live.ModelPose.Version);
        Assert.Equal(1u, EventCount(liveWorld, liveAnimation, 11));
        Assert.Equal(0u, EventCount(liveWorld, liveAnimation, 12));
    }

    [Fact]
    public void SelectedBlendAndLayersFollowDeclaredCurvesInIndependentPlayback()
    {
        var skeleton = new Skeleton([-1, 0, 1, 2, 0],
            [Bone(Vector3.Zero), Bone(new(0, 2, 0)), Bone(new(0, -1, 0)),
             Bone(new(0.5f, 0, 0)), Bone(new(0, 0, 3))], [0, 1, 2, 3, 4]);
        AnimationClip first = Clip(skeleton, 4, new(0, 2, 1, 0.2f, 0.1f), 21);
        AnimationClip second = Clip(skeleton, 4, new(2, -1, -0.5f, -0.4f, 0.2f), 22);
        AnimationClip overlay = Clip(skeleton, 4, new(10, 3, 2, 0.8f, 0.5f), 23);
        var mask = new BoneMask([0.25f, 0, 0.5f, 1, 0]);
        Keyframe[] keys = [new(0, 0), new(0.5f, 0.8f), new(1, 1)];
        PosePath blend = PosePath.Blend([
            new(PosePath.FromClip(first), new PoseWeightCurve(1, 0, 2)),
            new(PosePath.FromClip(second), new PoseWeightCurve(0, 1, 2)),
        ]);
        PosePath covered = PosePath.Layer(blend, PosePath.FromClip(overlay), mask, LayerMode.Override,
            new PoseWeightCurve(0.2f, 0.8f, 2, new Curve { Kind = CurveKind.Easing, Easing = EasingType.QuadIn }));
        PosePath selected = PosePath.Layer(covered, PosePath.FromClip(second), mask, LayerMode.Additive,
            new PoseWeightCurve(0.5f, 0.1f, 2, new Curve { Kind = CurveKind.Keyframes }, keyframes: keys));
        var request = new PosePredictionRequest(selected, new PoseTimeOrigin(20), Conditions: 57);
        short[] bones = [0, 3, 2];
        var batch = new PosePredictionBatch([new(request, bones), new(request, bones)]);
        var poses = new Matrix4x4[batch.OutputCount];
        var samples = new PosePredictionSample[batch.Count];
        batch.SampleModel([20.5, 21.5], calculationTime: 20, poses, samples);

        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance reference = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, reference, resultCount: 6, output: new(5));
        var layers = new AnimationLayers { Count = 3 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 0.2f };
        layers.Items[2] = new() { Mode = LayerMode.Additive, Weight = 0.5f };
        world.Add(target, layers);
        var coverWeight = Spec.Tween<float, FloatOps, QuadIn>(2);
        var addWeight = Spec.Keyframes<float, FloatOps>(2, keys);
        PropertyAnimation.To(world, target, OverrideWeight, 0.8f, coverWeight);
        PropertyAnimation.To(world, target, AdditiveWeight, 0.1f, addWeight);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.LayerPose(target, new(new(4), new(1), new(5), mask), layer: 2);
            builder.LayerPose(target, new(new(3), new(2), new(4), mask), layer: 1);
            builder.BlendPose(target, new(new PoseBlend([new(new(0), Parameter: 0), new(new(1), Parameter: 1)]),
                new(3), new(StageEnd.Settled)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(first, new(0)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(second, new(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(overlay, new(2)));
        }
        Entity animation = builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, coverWeight);
        AnimationSystems.Add(systems, addWeight);
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new PoseLayerSystem());
            nodes.Add(new PoseBlendSystem());
        });

        var graph = new GraphParameters();
        graph.Values[0] = 0.75f;
        graph.Values[1] = 0.25f;
        world.Replace(animation, graph);
        Tick(systems, world, 0.5);
        Match(reference, bones, poses.AsSpan(batch.GetOutputRange(0).Start, bones.Length));
        graph.Values[0] = 0.25f;
        graph.Values[1] = 0.75f;
        world.Replace(animation, graph);
        Tick(systems, world, 1);
        Match(reference, bones, poses.AsSpan(batch.GetOutputRange(1).Start, bones.Length));
        Assert.Equal(request, samples[1].Request);
        Assert.Equal(1.5f, samples[1].PathTime);
    }

    [Fact]
    public void StatefulPredictionCopiesTheCallersHistoryAndMatchesIndependentSpringPlayback()
    {
        var skeleton = new Skeleton([-1, 0, 1, 2, 0],
            [Bone(Vector3.Zero), Bone(new(0, 2, 0)), Bone(new(0, -1, 0)),
             Bone(new(0.5f, 0, 0)), Bone(new(0, 0, 3))], [0, 1, 2, 3, 4]);
        AnimationClip liveClip = Clip(skeleton, 4, new(0, 2, 1), 31);
        AnimationClip futureClip = Clip(skeleton, 4, new(10, 0, 0, 0, 0), 32);
        using var liveWorld = new World();
        using var referenceWorld = new World();
        var storage = new PoseStorage();
        using SkeletonInstance live = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(liveWorld, live);
        Entity liveAnimation;
        using (var builder = new AnimationBuilder(liveWorld))
        {
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(liveClip, new(0)));
            liveAnimation = builder.Play();
        }
        using var liveSystems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(liveWorld));
        PoseAnimation.AddSystems(liveSystems);
        Tick(liveSystems, liveWorld, 0.25);
        Timeline liveTime = liveWorld.Read<Timeline>(liveAnimation);
        Matrix4x4[] livePose = live.ModelPose.Transforms.ToArray();
        ulong liveVersion = live.ModelPose.Version;
        BoneTransform[] initialPose = live.LocalPose.ToArray();
        var initialHistory = new SpringPoseHistory { Springs = [new SpringState<Vector3>()] };
        var parameters = new SpringParameters();
        var request = new PosePredictionRequest(PosePath.FromClip(futureClip), new PoseTimeOrigin(10), Conditions: 73);
        var prediction = new PosePrediction<SpringParameters, SpringPoseHistory, SpringPoseAlgorithm>(
            new(request, [0, 3]), in parameters, in initialHistory, initialPose);

        Entity referenceRoot = referenceWorld.CreateEntity(new RootFollow { Position = initialPose[0].Translation });
        var spring = Spec.Spring<Vector3, Vector3Ops>();
        PropertyAnimation.To(referenceWorld, referenceRoot, RootPosition, new Vector3(10, 0, 0), spring);
        using var referenceSystems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(referenceWorld));
        AnimationSystems.Add(referenceSystems, spring);
        var poses = new Matrix4x4[2];
        PosePredictionSample first = prediction.AdvanceTo(10.25, calculationTime: 10.01, poses);
        Tick(referenceSystems, referenceWorld, 0.25);
        Vector3 expected = referenceWorld.Read<RootFollow>(referenceRoot).Position;
        Near(expected, poses[0].Translation);
        Near(expected + new Vector3(0.5f, 1, 0), poses[1].Translation);
        PosePredictionSample second = prediction.AdvanceTo(10.5, calculationTime: 10.02, poses);
        Tick(referenceSystems, referenceWorld, 0.25);
        expected = referenceWorld.Read<RootFollow>(referenceRoot).Position;
        Near(expected, poses[0].Translation);
        Near(expected + new Vector3(0.5f, 1, 0), poses[1].Translation);
        Assert.Equal(request, second.Request);
        Assert.Equal(0.25f, first.PathTime);
        Assert.Equal(0.5f, second.PathTime);
        Assert.Equal(10.02, second.CalculatedAt);
        Assert.Equal(Vector3.Zero, initialHistory.Springs[0].Velocity);
        Assert.Equal(live.LocalPose.ToArray(), initialPose);
        Assert.Equal(liveTime, liveWorld.Read<Timeline>(liveAnimation));
        Assert.Equal(livePose, live.ModelPose.Transforms.ToArray());
        Assert.Equal(liveVersion, live.ModelPose.Version);
        Assert.Equal(1u, EventCount(liveWorld, liveAnimation, 31));
        Assert.Equal(0u, EventCount(liveWorld, liveAnimation, 32));
    }

    private static AnimationClip Clip(Skeleton skeleton, float duration, Movement data, ushort marker) =>
        new(skeleton, duration, ClipSampler.Create<Movement, MovementSampler>(data), [new(0.1f, marker)], rootMotionBone: 0);

    private static Entity PlaySequence(World world, Entity target, AnimationClip first, AnimationClip second)
    {
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(first, new(0)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(second, new(0)));
        }
        return builder.Play();
    }

    private static BoneTransform Bone(Vector3 translation) =>
        new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var time = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref time);
    }

    private static uint EventCount(World world, Entity animation, ushort id)
    {
        var read = (Id: id, Count: 0u);
        world.ExecuteBufferRead<TimelineEvent, (ushort Id, uint Count)>(animation, ref read,
            static (events, ref read) =>
            {
                foreach (TimelineEvent marker in events.AsSpan())
                    if (marker.Id == read.Id) read.Count = marker.Count;
            });
        return read.Count;
    }

    private static void Match(SkeletonInstance reference, ReadOnlySpan<short> bones, ReadOnlySpan<Matrix4x4> poses)
    {
        for (int bone = 0; bone < bones.Length; bone++)
        {
            Matrix4x4 expected = reference.ModelPose.Transforms[bones[bone]];
            Matrix4x4 actual = poses[bone];
            Near(expected.Translation, actual.Translation);
            Near(Vector3.TransformNormal(Vector3.UnitX, expected), Vector3.TransformNormal(Vector3.UnitX, actual));
            Near(Vector3.TransformNormal(Vector3.UnitY, expected), Vector3.TransformNormal(Vector3.UnitY, actual));
            Near(Vector3.TransformNormal(Vector3.UnitZ, expected), Vector3.TransformNormal(Vector3.UnitZ, actual));
        }
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private sealed record Movement(float Start, float Speed, float FootSpeed,
        float RotationSpeed = 0.25f, float ScaleSpeed = 0.1f);

    private struct RootFollow : IComponent
    {
        public Vector3 Position;
    }

    private struct SpringPoseHistory
    {
        public SpringState<Vector3>[] Springs;
    }

    private readonly struct SpringPoseAlgorithm : IPosePredictionAlgorithm<SpringParameters, SpringPoseHistory>
    {
        public static SpringPoseHistory CopyState(in SpringPoseHistory initial) =>
            new() { Springs = initial.Springs.ToArray() };

        public static void Advance(in SpringParameters parameters, in MotionTime time,
            ReadOnlySpan<BoneTransform> target, Span<BoneTransform> pose, ref SpringPoseHistory state)
        {
            Vector3 current = pose[0].Translation;
            target.CopyTo(pose);
            var values = new MotionValues<Vector3>(current, target[0].Translation, current);
            pose[0].Translation = Spring<Vector3, Vector3Ops>.Evaluate(in parameters, in time, in values,
                ref state.Springs[0]).Value;
        }
    }

    private readonly struct MovementSampler : IClipSampler<Movement>
    {
        public static void SampleLocal(Movement data, float time, Span<BoneTransform> destination)
        {
            destination[0].Translation.X = data.Start + data.Speed * time;
            destination[0].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, time * data.RotationSpeed);
            destination[2].Translation.X = data.FootSpeed * time;
            destination[3].Scale = new(1 + time * data.ScaleSpeed);
        }
    }
}
