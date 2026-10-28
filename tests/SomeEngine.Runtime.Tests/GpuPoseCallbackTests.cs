using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Runtime.Tests;

[Collection("Scene content")]
public sealed class GpuPoseCallbackTests(SceneContentFixture content)
{
    [Fact]
    public async Task CanceledPoseCallbacksFollowTheSurvivingSkeletonAfterTargetRemoval()
    {
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var definition = new Skeleton([-1, 0, 1], [identity, identity, identity], [1, 2, 3]);
        QuantizedClipData clip = QuantizedClipData.Encode(GpuPoseExamples.Clip(definition, 0), 4);
        var storage = new PoseStorage();
        using SkeletonInstance removed = storage.CreateInstance(definition);
        using SkeletonInstance body = storage.CreateInstance(definition);
        Entity removedTarget = default, target = default;
        var readbacks = new List<GpuPoseReadback>();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                removedTarget = GpuPoseAnimation.CreateTarget(frame.World, removed);
                target = GpuPoseAnimation.CreateTarget(frame.World, body);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuClipParameters, GpuClipLeaf>(target, new(clip));
                builder.Play();
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    frame.Scene.ReadGpuPose(removedTarget, readbacks.Add);
                    frame.Scene.ReadGpuPose(target, readbacks.Add);
                    frame.Scene.CancelFrame();
                }
                if (frame.Index == 1)
                    frame.World.DestroyEntity(removedTarget);
            },
        };

        await SceneImages.RunAsync("d3d12", new SceneScript(application, 3, []),
            "--fixed-step", "0.25", "--no-taa", "--no-vsm");

        GpuPoseReadback result = Assert.Single(readbacks);
        Assert.Same(body, result.Identity.Instance);
        Assert.Equal(target, result.Identity.Target);
        Assert.Equal(new PoseRange(0, 3), result.Range);
        Assert.Equal(.5f, result.SampleTime);
        using SkeletonInstance reference = storage.CreateInstance(definition);
        var local = new BoneTransform[3];
        clip.CreateClip(definition).SampleLocal(result.SampleTime, local);
        reference.Update(local, result.CalculationTime);
        for (int bone = 0; bone < local.Length; bone++)
            GpuPoseExamples.Near(reference.ModelPose.GetBone(bone).Transform, result.Current.Span[bone]);
    }
}
