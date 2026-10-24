using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task GpuContinuesFromChangedCpuBoneDemandsAndAdvancingParentsWhileChildSourcesAreHeld()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var parentDefinition = new Skeleton([-1], [identity], [1]);
        var childDefinition = new Skeleton(parentDefinition, [-1, 0, 0], [identity, identity, identity], [2, 3, 4], [0, -1, -1]);
        QuantizedClipData parentData = QuantizedClipData.Encode(GpuPoseExamples.Clip(parentDefinition, 0), 4);
        QuantizedClipData childData = QuantizedClipData.Encode(GpuPoseExamples.Clip(childDefinition, 1), 4);
        var cpuSource = new AnimationClip(childDefinition, 2, ClipSampler.Create<CriticalClip, CriticalSampler>(new()));
        AnimationClip parentClip = parentData.CreateClip(parentDefinition), childClip = childData.CreateClip(childDefinition);
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance critical = storage.CreateInstance(childDefinition, parent);
        using SkeletonInstance visual = storage.CreateInstance(childDefinition, parent);
        Entity[] targets = new Entity[3], children = new Entity[2];
        var repeat = new GpuPoseExamples.RepeatLastInput(children);
        var cpuReads = new List<(Matrix4x4 Root, double Time, double SampleTime)>();
        var results = new List<GpuPoseReadback>();
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, repeat, AnimationPhase.Composition);
                AnimationSystems.Add(frame.Systems.Animation, new ReadCriticalBone(critical.ModelPose.GetBone(1), cpuReads), AnimationPhase.Completion);
                targets[0] = GpuPoseAnimation.CreateTarget(frame.World, parent);
                targets[1] = children[0] = GpuPoseAnimation.CreateTarget(frame.World, critical);
                targets[2] = children[1] = GpuPoseAnimation.CreateTarget(frame.World, visual);
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Parallel())
                {
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(targets[0], new(parentData));
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(targets[1], new(childData));
                    builder.Leaf<GpuClipParameters, GpuClipLeaf>(targets[2], new(childData));
                }
                builder.Play();
                GpuPoseAnimation.SetCpuBones(frame.World, targets[1], new CpuBoneDemand(cpuSource, [0]));
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? visual : critical, CriticalBinding(right)));
                }
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 1) GpuPoseAnimation.SetCpuBones(frame.World, targets[1], new CpuBoneDemand(cpuSource, [1]));
                repeat.Repeat = frame.Index >= 2;
            },
        };
        SceneImage? actual = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 4,
            [new(0, Read), new(1, Read), new(2, Read), new(3, edit => { Read(edit); edit.CaptureImage(value => actual = value); })]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        Assert.Equal(12, results.Count);
        for (int frame = 0; frame < 4; frame++)
        {
            float time = (frame + 1) * .25f, sourceTime = Math.Min(time, .5f);
            Matrix4x4[][] current = Reference(time, sourceTime);
            Matrix4x4[][] previous = frame == 0 ? current : Reference(time - .25f, Math.Min(time - .25f, .5f));
            GpuPoseExamples.Near(current[1][0], cpuReads[frame + 1].Root);
            Assert.Equal(time, cpuReads[frame + 1].Time);
            Assert.Equal(sourceTime, cpuReads[frame + 1].SampleTime);
            for (int section = 0; section < 3; section++)
            {
                GpuPoseReadback result = results[frame * 3 + section];
                Assert.Equal(1u, result.Starts);
                Assert.Equal(section == 0 ? (ulong)frame + 2 : (ulong)Math.Min(frame, 1) + 2, result.InputNumber);
                Assert.Equal(time, result.CalculationTime);
                Assert.Equal(section == 0 ? time : sourceTime, result.SourceCalculationTime);
                Assert.Equal(section == 0 ? time : sourceTime, result.SampleTime);
                if (frame > 0) Assert.True(result.Version > results[(frame - 1) * 3 + section].Version);
                for (int bone = 0; bone < current[section].Length; bone++)
                {
                    GpuPoseExamples.Near(current[section][bone], result.Current.Span[bone]);
                    GpuPoseExamples.Near(previous[section][bone], result.Previous.Span[bone]);
                    Assert.Equal(time, result.Calculations.Span[bone].Time);
                    Assert.Equal(section == 0 ? time : sourceTime, result.Calculations.Span[bone].SampleTime);
                }
            }
        }
        using SkeletonInstance referenceParent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance referenceCritical = storage.CreateInstance(childDefinition, referenceParent);
        using SkeletonInstance referenceVisual = storage.CreateInstance(childDefinition, referenceParent);
        var referenceApplication = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                UpdateReference(0);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? referenceVisual : referenceCritical, CriticalBinding(right)));
                }
            },
            UpdateFrame = frame => UpdateReference((frame.Index + 1) * .25f),
        };
        SceneImage? expected = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(referenceApplication, 4,
            [new(3, edit => edit.CaptureImage(value => expected = value))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        SceneImages.AssertEqualWithinPixel(Assert.IsType<SceneImage>(expected), Assert.IsType<SceneImage>(actual), tolerance: 2);

        void Read(SceneEdit edit) { foreach (Entity target in targets) edit.ReadGpuPose(target, results.Add); }
        Matrix4x4[][] Reference(float time, float sourceTime)
        {
            using SkeletonInstance body = storage.CreateInstance(parentDefinition);
            using SkeletonInstance mixed = storage.CreateInstance(childDefinition, body);
            using SkeletonInstance rest = storage.CreateInstance(childDefinition, body);
            Fill(body, mixed, rest, time, sourceTime);
            return [body.ModelPose.GetSection(0).Transforms.ToArray(), mixed.ModelPose.GetSection(1).Transforms.ToArray(),
                rest.ModelPose.GetSection(1).Transforms.ToArray()];
        }
        void UpdateReference(float time) => Fill(referenceParent, referenceCritical, referenceVisual, time, Math.Min(time, .5f));
        void Fill(SkeletonInstance body, SkeletonInstance mixed, SkeletonInstance rest, float time, float sourceTime)
        {
            var parentLocal = new BoneTransform[1];
            var local = new BoneTransform[3];
            var cpu = new BoneTransform[3];
            parentClip.SampleLocal(time, parentLocal); body.Update(parentLocal, time);
            childClip.SampleLocal(sourceTime, local); rest.Update(local, time, sourceTime);
            cpuSource.SampleLocal(sourceTime, cpu);
            local[0] = cpu[0];
            if (time >= .5f) local[1] = cpu[1];
            mixed.Update(local, time, sourceTime);
        }
    }

    private static SkinBinding CriticalBinding(bool right) => new([Matrix4x4.Identity, Matrix4x4.Identity], right ? [1, 3] : [1, 2]);
    private sealed class CriticalClip;
    private readonly struct CriticalSampler : IClipSampler<CriticalClip>
    {
        public static void SampleLocal(CriticalClip data, float time, Span<BoneTransform> destination)
        {
            destination[0] = new() { Translation = new(1 + time, .3f * time, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            destination[1] = new() { Translation = new(0, 1 + time, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        }
    }
    private sealed class ReadCriticalBone(BonePose bone, List<(Matrix4x4, double, double)> values) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => values.Add((bone.Transform, bone.Time, bone.SampleTime));
    }
}
