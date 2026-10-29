using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task ReducedGpuSamplingInterpolatesSelectedBonesWhileCriticalBonesAndSourceSignalsAdvance()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform identity = At(Vector3.Zero);
        var skeleton = new Skeleton([-1, 0, 0, 0],
            [identity, At(Vector3.UnitY), At(Vector3.UnitY), At(-Vector3.UnitY)], [1, 2, 3, 4]);
        var raw = new AnimationClip(skeleton, 1, ClipSampler.Create<FrequencyClip, FrequencySampler>(new()),
            [new(.125f, 1), new(.625f, 2)], 0);
        QuantizedClipData data = QuantizedClipData.Encode(raw, 4);
        AnimationClip clip = data.CreateClip(skeleton);
        var storage = new PoseStorage();
        using SkeletonInstance body = storage.CreateInstance(skeleton), referenceBody = storage.CreateInstance(skeleton);
        var parentDefinition = new Skeleton([-1, 0, 0], [identity, identity, identity], [5, 6, 7]);
        var childDefinition = new Skeleton(parentDefinition, [-1, 0], [identity, identity], [8, 9], [1, -1]);
        QuantizedClipData parentData = QuantizedClipData.Encode(GpuPoseExamples.Clip(parentDefinition, 0), 4);
        QuantizedClipData childData = QuantizedClipData.Encode(GpuPoseExamples.Clip(childDefinition, 1), 4);
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition), child = storage.CreateInstance(childDefinition, parent);
        var secondaryDefinition = new Skeleton([-1, 0], [identity, At(Vector3.UnitX)], [10, 11]);
        using SkeletonInstance secondary = storage.CreateInstance(secondaryDefinition);
        var secondaryClip = QuantizedClipData.Encode(new AnimationClip(secondaryDefinition, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var secondarySource = new GpuPoseBlend(secondaryDefinition, new GpuBlendClip(secondaryClip, Loop: true));
        Entity target = default, animation = default, parentTarget = default, childTarget = default;
        var signals = new FrequencySignals(body);
        var reads = new List<GpuPoseReadback>();
        var parentReads = new List<GpuPoseReadback>();
        SceneImage? actual = null, expected = null;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, signals, AnimationPhase.Completion);
                target = GpuPoseAnimation.CreateTarget(frame.World, body);
                parentTarget = GpuPoseAnimation.CreateTarget(frame.World, parent);
                childTarget = GpuPoseAnimation.CreateTarget(frame.World, child);
                Entity secondaryTarget = GpuPoseAnimation.CreateTarget(frame.World, secondary);
                signals.Target = target;
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Parallel())
                {
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(target, new(data, Loop: true, RootMotion: RootMotionMode.Extract));
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(parentTarget, new(parentData));
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(childTarget, new(childData));
                    builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(secondaryTarget, new(new GpuXpbdPose(secondarySource,
                        new(secondaryDefinition, [new(0), new(1)], Vector3.Zero))));
                }
                animation = builder.Play();
                signals.Animation = animation;
                GpuPoseAnimation.SetEvaluation(frame.World, target, new GpuPoseEvaluation(.5f, [2]));
                GpuPoseAnimation.SetCpuBones(frame.World, target, new CpuBoneDemand(clip, [1]));
                GpuPoseAnimation.SetEvaluation(frame.World, parentTarget, new GpuPoseEvaluation(10, [0]));
                GpuPoseAnimation.SetEvaluation(frame.World, childTarget, new GpuPoseEvaluation(0, []));
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(body, new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [2, 3])));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 4)
                {
                    GpuPoseAnimation.SetEvaluation(frame.World, target, new GpuPoseEvaluation(.25f, [3]));
                    GpuPoseAnimation.SetEvaluation(frame.World, childTarget, new GpuPoseEvaluation(0, [1]));
                }
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 8,
            Enumerable.Range(0, 8).Select(index => new SceneAction(index, edit =>
            {
                edit.ReadGpuPose(target, reads.Add);
                if (index is 3 or 4) edit.ReadGpuPose(parentTarget, parentReads.Add);
                if (index == 7) edit.CaptureImage(value => actual = value);
            }))), "--fixed-step", "0.25", "--no-taa", "--no-vsm");
        Assert.Equal(8, reads.Count);
        for (int frame = 0; frame < 8; frame++)
        {
            float time = (frame + 1) * .25f;
            GpuPoseReadback result = reads[frame];
            var endpoints = Endpoints(frame);
            Assert.Equal(endpoints.Before, result.Presentation.PreviousTime);
            Assert.Equal(endpoints.After, result.Presentation.CurrentTime);
            Assert.Equal(endpoints.Amount, result.Presentation.Amount);
            Assert.Equal(reads[(int)(endpoints.Before / .25f) - 1].Identity, result.Presentation.PreviousInput);
            Assert.Equal(reads[(int)(endpoints.After / .25f) - 1].Identity, result.Presentation.CurrentInput);
            int sampledBone = frame < 4 ? 2 : 3;
            Assert.Equal(endpoints.Before % 1, result.SourceSamples.Span[sampledBone].PreviousTime);
            Assert.Equal(endpoints.After % 1, result.SourceSamples.Span[sampledBone].CurrentTime);
            Assert.Equal(frame < 4 ? new short[] { 2 } : new short[] { 3 }, result.SampledBones.ToArray());
            Assert.Equal(time, result.CalculationTime);
            Assert.Equal(time, signals.Critical[frame + 1].Time);
            Assert.Equal(time % 1, signals.Critical[frame + 1].SampleTime);
            GpuPoseExamples.Near(Matrix4x4.Identity, signals.Critical[frame + 1].Root);
            BoneTransform[] local = Reference(frame);
            referenceBody.Update(local, time);
            for (int bone = 0; bone < 4; bone++)
                GpuPoseExamples.Near(referenceBody.ModelPose.GetBone(bone).Transform, result.Current.Span[bone]);
        }
        Assert.Equal(new short[] { 0 }, parentReads[0].SampledBones.ToArray());
        Assert.Equal(new short[] { 0, 1 }, parentReads[1].SampledBones.ToArray());
        Assert.Equal(1.25, parentReads[1].Presentation.PreviousTime);
        Assert.Equal(1.25, parentReads[1].Presentation.CurrentTime);
        var parentLocal = new BoneTransform[3];
        parentData.CreateClip(parentDefinition).SampleLocal(1.25f, parentLocal);
        parentLocal[2] = parentDefinition.ReferencePose[2];
        using (SkeletonInstance referenceParent = storage.CreateInstance(parentDefinition))
        {
            referenceParent.Update(parentLocal, 1.25);
            for (int bone = 0; bone < 3; bone++)
                GpuPoseExamples.Near(referenceParent.ModelPose.GetBone(bone).Transform, parentReads[1].Current.Span[bone]);
        }
        using (var referenceWorld = new World())
        using (SkeletonInstance signalBody = storage.CreateInstance(skeleton))
        using (var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(referenceWorld)))
        {
            Entity source = PoseAnimation.CreateTarget(referenceWorld, signalBody);
            using var builder = new AnimationBuilder(referenceWorld);
            builder.Leaf<ClipParameters, ClipLeaf>(source, new(clip, new(0), Loop: true, RootMotion: RootMotionMode.Extract));
            Entity sourceAnimation = builder.Play();
            PoseAnimation.AddSystems(systems);
            var update = new ImmediateSystemContext(referenceWorld, 0, 0, 2);
            systems.Update(ref update);
            GpuPoseExamples.Near(PoseAnimation.ReadResult(referenceWorld, PoseAnimation.Resolve(referenceWorld, source, new(0))).RootMotion.Transform,
                signals.Motion);
            referenceWorld.ExecuteBufferRead<TimelineEvent>(sourceAnimation, events =>
            {
                foreach (TimelineEvent marker in events.AsSpan()) Assert.Equal(marker.Count, signals.Events[marker.Id]);
            });
        }
        var referenceApplication = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                referenceBody.Update(Reference(0), .25);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(referenceBody, new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [2, 3])));
            },
            UpdateFrame = frame => referenceBody.Update(Reference(frame.Index), (frame.Index + 1) * .25),
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(referenceApplication, 8,
            [new(7, edit => edit.CaptureImage(value => expected = value))]), "--fixed-step", "0.25", "--no-taa", "--no-vsm");
        SceneImages.AssertEqualWithinPixel(Assert.IsType<SceneImage>(expected), Assert.IsType<SceneImage>(actual), tolerance: 2);

        BoneTransform[] Reference(int frame)
        {
            float time = (frame + 1) * .25f;
            var current = new BoneTransform[4];
            clip.SampleLocal(time % 1, current);
            current[0] = identity;
            var endpoints = Endpoints(frame);
            var before = new BoneTransform[4]; var after = new BoneTransform[4];
            clip.SampleLocal(endpoints.Before % 1, before); clip.SampleLocal(endpoints.After % 1, after);
            int selected = frame < 4 ? 2 : 3;
            current[selected] = new()
            {
                Translation = Vector3.Lerp(before[selected].Translation, after[selected].Translation, endpoints.Amount),
                Rotation = Quaternion.Slerp(before[selected].Rotation, after[selected].Rotation, endpoints.Amount),
                Scale = Vector3.Lerp(before[selected].Scale, after[selected].Scale, endpoints.Amount),
            };
            current[frame < 4 ? 3 : 2] = skeleton.ReferencePose[frame < 4 ? 3 : 2];
            return current;
        }
        static (float Before, float After, float Amount) Endpoints(int frame) => frame switch
        {
            0 or 1 => (.25f, .25f, 1), 2 => (.25f, .75f, 0), 3 => (.25f, .75f, .5f),
            4 => (1.25f, 1.25f, 1), _ => (frame * .25f, (frame + 1) * .25f, 0),
        };
    }
    private static BoneTransform At(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
    private sealed class FrequencyClip;
    private readonly struct FrequencySampler : IClipSampler<FrequencyClip>
    {
        public static void SampleLocal(FrequencyClip data, float time, Span<BoneTransform> destination)
        {
            destination[0] = At(new(.4f * time, .1f * time, 0));
            destination[0].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f * time);
            destination[1] = At(new(0, 1 + .4f * time, 0));
            destination[2] = At(new(.2f * time * time, 1, 0));
            destination[2].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f * time);
            destination[2].Scale = new(1 + .1f * time, 1, 1);
            destination[3] = At(new(-.3f * time * time, -1 - .3f * time, 0));
            destination[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.4f * time);
        }
    }
    private sealed class FrequencySignals(SkeletonInstance body) : ISystem<ImmediateSystemContext>
    {
        internal Entity Target, Animation;
        internal Matrix4x4 Motion = Matrix4x4.Identity;
        internal readonly Dictionary<ushort, uint> Events = [];
        internal readonly List<(Matrix4x4 Root, double Time, double SampleTime)> Critical = [];
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Motion = context.World.Read<GpuPoseTarget>(Target).RootMotion.Transform * Motion;
            BonePose critical = body.ModelPose.GetBone(1);
            Critical.Add((body.ModelPose.GetBone(0).Transform, critical.Time, critical.SampleTime));
            context.World.ExecuteBufferRead<TimelineEvent>(Animation, values =>
            {
                foreach (TimelineEvent marker in values.AsSpan()) Events[marker.Id] = marker.Count;
            });
        }
    }
}
