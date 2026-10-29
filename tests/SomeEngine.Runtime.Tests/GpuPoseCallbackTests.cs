using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Runtime.Tests;

[Collection("Scene content")]
public sealed class GpuPoseCallbackTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuPoseReadbacksRestartAfterFramesWithAnEmptySkeletonSet()
    {
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var definition = new Skeleton([-1, 0, 1], [identity, identity, identity], [1, 2, 3]);
        QuantizedClipData clip = QuantizedClipData.Encode(GpuPoseExamples.Clip(definition, 0), 4);
        var storage = new PoseStorage();
        using SkeletonInstance body = storage.CreateInstance(definition);
        Entity target = default, animation = default;
        var targets = new List<Entity>();
        var readbacks = new List<GpuPoseReadback>();
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Initialize = frame => GpuPoseAnimation.AddSystems(frame.Systems.Animation),
            UpdateFrame = frame =>
            {
                if (frame.Index is 1 or 5)
                {
                    target = GpuPoseAnimation.CreateTarget(frame.World, body);
                    targets.Add(target);
                    using var builder = new AnimationBuilder(frame.World);
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(target, new(clip));
                    animation = builder.Play();
                }
                if (frame.Index is 1 or 2 or 5 or 6)
                    frame.Scene.ReadGpuPose(target, readbacks.Add);
                if (frame.Index == 3)
                {
                    PropertyAnimation.Stop(frame.World, animation);
                    frame.World.DestroyEntity(target);
                }
            },
        };

        await SceneImages.RunAsync("d3d12", new SceneScript(application, 7, []),
            "--fixed-step", "0.25", "--no-taa", "--no-vsm");

        Assert.Equal(4, readbacks.Count);
        using SkeletonInstance reference = storage.CreateInstance(definition);
        AnimationClip referenceClip = clip.CreateClip(definition);
        var local = new BoneTransform[3];
        for (int index = 0; index < readbacks.Count; index++)
        {
            GpuPoseReadback result = readbacks[index];
            Assert.Equal(targets[index / 2], result.Identity.Target);
            Assert.Same(body, result.Identity.Instance);
            Assert.Equal(new PoseRange(0, 3), result.Range);
            referenceClip.SampleLocal(result.SampleTime, local);
            reference.Update(local, result.CalculationTime);
            for (int bone = 0; bone < local.Length; bone++)
            {
                GpuPoseExamples.Near(reference.ModelPose.GetBone(bone).Transform, result.Current.Span[bone]);
                Matrix4x4 previous = index % 2 == 0 ? result.Current.Span[bone] : readbacks[index - 1].Current.Span[bone];
                GpuPoseExamples.Near(previous, result.Previous.Span[bone]);
            }
            if (index % 2 == 1)
                Assert.True(result.SampleTime > readbacks[index - 1].SampleTime);
        }
    }

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
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
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
