using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class RetargetingBehaviorTests
{
    [Fact]
    public void ChainMappedMotionKeepsConfiguredGoalsAndRootScaleAcrossProportionsThroughSamplingAndGraphPlayback()
    {
        // Reach, broad and tall are example motion/body fixtures for the reusable chain mapping.
        Skeleton source = Body([1, 1], Quaternion.Identity);
        Quaternion axes = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Skeleton broad = Body([1.5f, .5f], axes), tall = Body([.75f, 1, 1.25f], axes);
        var reach = new AnimationClip(source, 1, ClipSampler.Create<Skeleton, ReachSampler>(source), rootMotionBone: 0);
        var broadMap = new RetargetMapping(source, broad, new(0, 0, Quaternion.Identity, axes, new(2)),
            [new([1, 2, 3], [1, 2, 3], Quaternion.Identity, axes, GoalOffset: new(.1f, -.1f, 0))],
            settings: [new(0, PositionStiffness: 1, RotationStiffness: 1)], tolerance: .0001f, iterations: 256);
        var tallMap = new RetargetMapping(source, tall, new(0, 0, Quaternion.Identity, axes, new(3)),
            [new([1, 2, 3], [1, 2, 3, 4], Quaternion.Identity, axes, GoalOffset: new(-.1f, -.1f, 0))],
            settings: [new(0, PositionStiffness: 1, RotationStiffness: 1)], tolerance: .0001f, iterations: 256);
        AnimationClip broadClip = broadMap.CreateClip(reach), tallClip = tallMap.CreateClip(reach);
        var batch = new ClipSampleBatch([new(broadClip), new(broadClip), new(tallClip), new(tallClip)]);
        BoneTransform[] local = new BoneTransform[batch.OutputCount];
        Matrix4x4[] model = new Matrix4x4[batch.OutputCount];
        batch.SampleLocal([.5f, 1, .5f, 1], local);
        batch.SampleModel([.5f, 1, .5f, 1], model);
        Near(new(0, .5f, 0), local[0].Translation);
        Near(new(0, 1, 0), local[4].Translation);
        Near(new(0, .75f, 0), local[8].Translation);
        Near(new(0, 1.5f, 0), local[13].Translation);
        Near(new(.1f, 2.1071068f, -.7071068f), model[3].Translation);
        Near(new(.1f, 1.9f, -1), model[7].Translation);
        Near(new(-.1f, 3.2106602f, -1.0606602f), model[12].Translation);
        Near(new(-.1f, 2.9f, -1.5f), model[17].Translation);
        Near(new(-.5f, 0, -.8660254f), Vector3.TransformNormal(Vector3.UnitX, model[7]));
        Near(new(-.5f, 0, -.8660254f), Vector3.TransformNormal(Vector3.UnitX, model[17]));
        Near(new(1.5f, 0, 0), local[6].Translation);
        Near(new(.5f, 0, 0), local[7].Translation);
        Near(new(.75f, 0, 0), local[15].Translation);
        Near(new(1, 0, 0), local[16].Translation);
        Near(new(1.25f, 0, 0), local[17].Translation);

        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(broad), second = storage.CreateInstance(tall);
        Entity firstTarget = PoseAnimation.CreateTarget(world, first, 2, new(1));
        Entity secondTarget = PoseAnimation.CreateTarget(world, second, 2, new(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(firstTarget, new(new PoseBlend([new(new(0))]), new(1), new(StageEnd.Settled)));
            builder.BlendPose(secondTarget, new(new PoseBlend([new(new(0))]), new(1), new(StageEnd.Settled)));
            builder.Leaf<ClipParameters, ClipLeaf>(firstTarget, new(broadClip, new(0), RootMotion: RootMotionMode.Extract));
            builder.Leaf<ClipParameters, ClipLeaf>(secondTarget, new(tallClip, new(0), RootMotion: RootMotionMode.Extract));
        }
        builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PoseBlendSystem()));
        PoseResultReference firstOutput = PoseAnimation.Resolve(world, firstTarget, new(1));
        PoseResultReference secondOutput = PoseAnimation.Resolve(world, secondTarget, new(1));
        var context = new ImmediateSystemContext(world, 0, 0, .5);
        systems.Update(ref context);
        Matrix4x4 firstMovement = PoseAnimation.ReadResult(world, in firstOutput).RootMotion.Transform;
        Matrix4x4 secondMovement = PoseAnimation.ReadResult(world, in secondOutput).RootMotion.Transform;
        Near(new(0, .5f, 0), firstMovement.Translation);
        Near(new(0, .75f, 0), secondMovement.Translation);
        Near(new(.1f, 2.1071068f, -.7071068f), Vector3.Transform(first.ModelPose.GetBone(3).Transform.Translation, firstMovement));
        Near(new(-.1f, 3.2106602f, -1.0606602f), Vector3.Transform(second.ModelPose.GetBone(4).Transform.Translation, secondMovement));
        systems.Update(ref context);
        firstMovement = PoseAnimation.ReadResult(world, in firstOutput).RootMotion.Transform * firstMovement;
        secondMovement = PoseAnimation.ReadResult(world, in secondOutput).RootMotion.Transform * secondMovement;
        Near(new(0, 1, 0), firstMovement.Translation);
        Near(new(0, 1.5f, 0), secondMovement.Translation);
        Near(new(.1f, 1.9f, -1), Vector3.Transform(first.ModelPose.GetBone(3).Transform.Translation, firstMovement));
        Near(new(-.1f, 2.9f, -1.5f), Vector3.Transform(second.ModelPose.GetBone(4).Transform.Translation, secondMovement));
        Near(Vector3.Zero, first.LocalPose[0].Translation);
        Near(Vector3.Zero, second.LocalPose[0].Translation);
        Assert.Equal(1, first.ModelPose.SampleTime);
        Assert.Equal(1, second.ModelPose.SampleTime);
    }

    private static Skeleton Body(float[] lengths, Quaternion axes)
    {
        short[] parents = new short[lengths.Length + 2];
        BoneTransform[] reference = new BoneTransform[parents.Length];
        uint[] names = new uint[parents.Length];
        for (int bone = 0; bone < parents.Length; bone++)
        {
            parents[bone] = (short)(bone - 1);
            reference[bone] = new() { Translation = bone < 2 ? Vector3.Zero : new(lengths[bone - 2], 0, 0),
                Rotation = bone == 0 ? axes : Quaternion.Identity, Scale = Vector3.One };
            names[bone] = (uint)bone + 1;
        }
        return new(parents, reference, names);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < .0003f, $"Expected {expected}; actual {actual}.");

    private readonly struct ReachSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation = new(.5f * time, 0, 0);
            destination[2].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2 * time);
            destination[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6 * time);
        }
    }
}
