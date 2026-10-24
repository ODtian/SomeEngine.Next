using System.Numerics;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class ClipBatchSamplingTests
{
    [Fact]
    public void SelectedBonesAtSeveralTimesMatchCompletePosesWhileCharactersKeepTheirCurrentPose()
    {
        var skeleton = new Skeleton([-1, 0, 1, 0],
            [Bone(new(2, 0, 0)), Bone(new(0, 2, 0)), Bone(Vector3.UnitX), Bone(new(0, 0, 3))],
            [0, 0, 0, 0]);
        var data = new RawClipData(2,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 2],
                [new(2, 0, 0, 0), new(8, 0, 0, 0)]),
            new(1, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 2],
                [new(0, 0, 0, 1), new(0, 0, 0.70710677f, 0.70710677f)]),
            new(2, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 2],
                [new(1, 0, 0, 0), new(3, 0, 0, 0)]),
        ]);
        var firstClip = new AnimationClip(skeleton, 2, ClipSampler.Create<RawClipData, RawClipSampler>(data));
        var secondClip = new AnimationClip(skeleton, 2,
            ClipSampler.Create<TranslationData, TranslationSampler>(new(-2)));
        ClipSampleSelection[] selections =
        [
            new(firstClip, [2, 0]), new(secondClip, [3, 1]),
            new(firstClip, [3, 2]), new(secondClip),
        ];
        var batch = new ClipSampleBatch(selections);
        float[] times = [0.5f, 1.5f, 1.25f, 0.75f];
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        BoneTransform[] complete = new BoneTransform[skeleton.BoneCount];
        firstClip.SampleLocal(0.25f, complete);
        first.Update(complete, 7.25);
        secondClip.SampleLocal(1, complete);
        second.Update(complete, 8.5);
        BoneTransform[] firstLocal = first.LocalPose.ToArray(), secondLocal = second.LocalPose.ToArray();
        Matrix4x4[] firstModel = first.ModelPose.Transforms.ToArray(), secondModel = second.ModelPose.Transforms.ToArray();
        ulong firstVersion = first.ModelPose.Version, secondVersion = second.ModelPose.Version;
        BoneTransform untouchedLocal = Bone(new(-99, -88, -77));
        Matrix4x4 untouchedModel = untouchedLocal.ToMatrix();
        BoneTransform[] local = Enumerable.Repeat(untouchedLocal, batch.OutputCount + 2).ToArray();
        Matrix4x4[] model = Enumerable.Repeat(untouchedModel, batch.OutputCount + 2).ToArray();

        batch.SampleLocal(times, local.AsSpan(1, batch.OutputCount));
        batch.SampleModel(times, model.AsSpan(1, batch.OutputCount));

        using SkeletonInstance reference = storage.CreateInstance(skeleton);
        for (int sample = 0; sample < selections.Length; sample++)
        {
            ClipSampleSelection selection = selections[sample];
            selection.Clip.SampleLocal(times[sample], complete);
            reference.Update(complete, times[sample]);
            PoseRange output = batch.GetOutputRange(sample);
            for (int selected = 0; selected < selection.Bones.Length; selected++)
            {
                int bone = selection.Bones[selected];
                Assert.Equal(complete[bone], local[1 + output.Start + selected]);
                Assert.Equal(reference.ModelPose.Transforms[bone], model[1 + output.Start + selected]);
            }
        }
        Assert.Equal(untouchedLocal, local[0]);
        Assert.Equal(untouchedLocal, local[^1]);
        Assert.Equal(untouchedModel, model[0]);
        Assert.Equal(untouchedModel, model[^1]);
        Assert.Equal(firstLocal, first.LocalPose.ToArray());
        Assert.Equal(secondLocal, second.LocalPose.ToArray());
        Assert.Equal(firstModel, first.ModelPose.Transforms.ToArray());
        Assert.Equal(secondModel, second.ModelPose.Transforms.ToArray());
        Assert.Equal(firstVersion, first.ModelPose.Version);
        Assert.Equal(secondVersion, second.ModelPose.Version);
        Assert.Equal(7.25, first.ModelPose.Time);
        Assert.Equal(8.5, second.ModelPose.Time);
    }

    private static BoneTransform Bone(Vector3 position)
        => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    private sealed record TranslationData(float Speed);

    private readonly struct TranslationSampler : IClipSampler<TranslationData>
    {
        public static void SampleLocal(TranslationData data, float time, Span<BoneTransform> destination)
            => destination[0].Translation.X = data.Speed * time;
    }
}
