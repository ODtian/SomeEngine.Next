using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task SharedGpuControlsAdvanceIndependentStatesAndCrossfadesOncePerInputThroughTheApplication()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var firstRaw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [Vector4.Zero, new(2, 0, 0, 0)]),
            new(1, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
        ]);
        var secondRaw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [Vector4.Zero, new(0, 2, 0, 0)]),
            new(1, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 1), new(0, 0, -.70710677f, .70710677f)]),
        ]);
        QuantizedClipData firstClip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(firstRaw)), 4);
        QuantizedClipData secondClip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(secondRaw)), 4);
        var machine = new GpuPoseStateMachine([new(firstClip, Loop: true), new(secondClip)],
        [
            new(0, 1, 0, GpuPoseComparison.GreaterOrEqual, .5f, .5f),
            new(1, 0, 0, GpuPoseComparison.Less, .5f),
        ]);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [0, 1]);
        Entity[] targets = new Entity[2], animations = new Entity[2];
        var repeat = new GpuPoseExamples.RepeatLastInput(targets);
        var reads = new List<GpuPoseReadback>();
        bool completed = false;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, repeat, AnimationPhase.Completion);
                targets[0] = GpuPoseAnimation.CreateTarget(frame.World, first);
                targets[1] = GpuPoseAnimation.CreateTarget(frame.World, second);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuStateParameters, GpuStateLeaf>(Entity.Null, new(machine, Duration: 2));
                using CompiledGraph graph = builder.Compile();
                animations[0] = graph.Play(frame.World, targets[0]);
                animations[1] = graph.Play(frame.World, targets[1]);
                float control = 1;
                frame.World.ExecuteBufferWrite<GpuStateInstance, float>(animations[1], ref control,
                    static (instances, ref control) => instances[0].Controls[0] = control);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? second : first, binding));
                }
            },
            UpdateFrame = frame =>
            {
                repeat.Repeat = frame.Index == 5;
                if (frame.Index == 3)
                {
                    float control = 1;
                    frame.World.ExecuteBufferWrite<GpuStateInstance, float>(animations[0], ref control,
                        static (instances, ref control) => instances[0].Controls[0] = control);
                }
                if (frame.Index == 4)
                {
                    float control = 0;
                    frame.World.ExecuteBufferWrite<GpuStateInstance, float>(animations[1], ref control,
                        static (instances, ref control) => instances[0].Controls[0] = control);
                }
                if (frame.Index == 8)
                    completed = !frame.World.IsEnabled<Timeline>(animations[0]) && !frame.World.IsEnabled<Timeline>(animations[1]);
            },
        };
        SceneImage? animated = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 9,
            [new(0, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(3, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); edit.CaptureImage(image => animated = image); }),
             new(4, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(5, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(8, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); })]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        Assert.Equal(10, reads.Count);
        Assert.True(completed);
        Assert.Equal(0, reads[0].Playback.State);
        Assert.Equal(.25f, reads[0].SampleTime);
        Assert.Equal(1, reads[1].Playback.State);
        Assert.Equal(0, reads[1].Playback.SourceState);
        Assert.Equal(.25f, reads[1].Playback.Time);
        Assert.Equal(.25f, reads[1].Playback.SourceSampleTime);
        Assert.Equal(.5f, reads[1].Playback.TransitionWeight);
        GpuPoseExamples.Near(Matrix4x4.CreateTranslation(.5f, 0, 0), reads[0].Current.Span[0]);
        GpuPoseExamples.Near(Matrix4x4.CreateTranslation(.25f, .25f, 0), reads[1].Current.Span[0]);

        Assert.Equal(1, reads[2].Playback.State);
        Assert.Equal(0, reads[2].Playback.SourceState);
        Assert.Equal(1f, reads[2].Playback.SourceTime);
        Assert.Equal(0f, reads[2].Playback.SourceSampleTime);
        Assert.Equal(.25f, reads[2].SampleTime);
        Assert.Equal(.5f, reads[2].Playback.TransitionWeight);
        Assert.Equal(1, reads[3].Playback.State);
        Assert.Equal(1f, reads[3].SampleTime);
        Assert.Equal(1f, reads[3].Playback.TransitionWeight);
        Matrix4x4[][] current =
        [
            [Matrix4x4.CreateTranslation(0, .25f, 0),
                Matrix4x4.CreateRotationZ(-MathF.PI / 16) * Matrix4x4.CreateTranslation(0, 1.25f, 0)],
            [Matrix4x4.CreateTranslation(0, 2, 0),
                Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 3, 0)],
        ];
        Matrix4x4[][] previous =
        [
            [Matrix4x4.CreateTranslation(1.5f, 0, 0),
                Matrix4x4.CreateRotationZ(3 * MathF.PI / 8) * Matrix4x4.CreateTranslation(1.5f, 1, 0)],
            [Matrix4x4.CreateTranslation(0, 1.5f, 0),
                Matrix4x4.CreateRotationZ(-3 * MathF.PI / 8) * Matrix4x4.CreateTranslation(0, 2.5f, 0)],
        ];
        for (int character = 0; character < 2; character++)
            for (int bone = 0; bone < 2; bone++)
            {
                GpuPoseExamples.Near(current[character][bone], reads[character + 2].Current.Span[bone]);
                GpuPoseExamples.Near(previous[character][bone], reads[character + 2].Previous.Span[bone]);
            }
        Assert.Equal(1, reads[4].Playback.State);
        Assert.Equal(.5f, reads[4].SampleTime);
        Assert.Equal(1f, reads[4].Playback.TransitionWeight);
        Assert.Equal(0, reads[5].Playback.State);
        Assert.Equal(.25f, reads[5].SampleTime);
        for (int character = 0; character < 2; character++)
        {
            GpuPoseReadback before = reads[4 + character], repeated = reads[6 + character];
            Assert.Equal(before.Starts, repeated.Starts);
            Assert.Equal(before.InputNumber, repeated.InputNumber);
            Assert.Equal(before.CalculationTime, repeated.CalculationTime);
            Assert.Equal(before.SampleTime, repeated.SampleTime);
            Assert.Equal(before.Playback, repeated.Playback);
            for (int bone = 0; bone < 2; bone++)
            {
                GpuPoseExamples.Near(before.Current.Span[bone], repeated.Current.Span[bone]);
                GpuPoseExamples.Near(before.Previous.Span[bone], repeated.Previous.Span[bone]);
            }
        }
        Assert.Equal(1, reads[8].Playback.State);
        Assert.Equal(1.25f, reads[8].Playback.Time);
        Assert.Equal(1f, reads[8].SampleTime);
        Assert.Equal(0, reads[9].Playback.State);
        Assert.Equal(1f, reads[9].Playback.Time);
        Assert.Equal(0f, reads[9].SampleTime);

        Matrix4x4[][] visibleCurrent = [ReferencePose(0, .25f, .5f), ReferencePose(0, 1, 1)];
        Matrix4x4[][] visiblePrevious = [ReferencePose(.75f, 0, 0), ReferencePose(0, .75f, 1)];
        await content.WriteReferenceMeshesAsync((prior, right) =>
        {
            Matrix4x4[] pose = (prior ? visiblePrevious : visibleCurrent)[right ? 1 : 0];
            return (pose[0], Matrix4x4.CreateTranslation(0, -1, 0) * pose[1]);
        });
        await content.SetReferenceShaderAsync();
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Project.RootPath, 4,
            [new(3, edit => edit.CaptureImage(image => reference = image))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        SceneImage expected = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in expected.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
        SceneImages.AssertEqual(expected, Assert.IsType<SceneImage>(animated), tolerance: 2);

        Matrix4x4[] ReferencePose(float sourceTime, float targetTime, float weight)
        {
            var source = new BoneTransform[2];
            var target = new BoneTransform[2];
            firstClip.CreateClip(skeleton).SampleLocal(sourceTime, source);
            secondClip.CreateClip(skeleton).SampleLocal(targetTime, target);
            for (int bone = 0; bone < source.Length; bone++)
            {
                source[bone].Translation = Vector3.Lerp(source[bone].Translation, target[bone].Translation, weight);
                source[bone].Rotation = Quaternion.Slerp(source[bone].Rotation, target[bone].Rotation, weight);
                source[bone].Scale = Vector3.Lerp(source[bone].Scale, target[bone].Scale, weight);
            }
            using SkeletonInstance referenceBody = storage.CreateInstance(skeleton);
            referenceBody.Update(source, 0);
            return referenceBody.ModelPose.GetSection(0).Transforms.ToArray();
        }
    }
}
