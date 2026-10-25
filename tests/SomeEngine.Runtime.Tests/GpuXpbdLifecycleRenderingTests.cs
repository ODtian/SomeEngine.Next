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
    public async Task StoppedAndCompletedSecondaryChainsHoldTheirPresentedPoseUntilReplay()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        // These two three-bone tails are a fixture for the common leaf lifecycle.
        var skeleton = new Skeleton([-1, 0, 1],
            [Bone(Vector3.Zero), Bone(new(.6f, 0, 0)), Bone(new(.6f, 0, 0))], [1, 2, 3]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(QuantizedClipData.Encode(clip, 4), Loop: true));
        var pose = new GpuXpbdPose(source, new(skeleton, [new(0), new(1), new(2)], new(0, -6, 0), step: .05f));
        var storage = new PoseStorage();
        using SkeletonInstance stopped = storage.CreateInstance(skeleton), completed = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [stopped, completed];
        Entity[] targets = new Entity[2], animations = new Entity[2];
        CompiledGraph[] graphs = new CompiledGraph[2];
        var reads = new List<GpuPoseReadback>();
        var binding = new SkinBinding([Matrix4x4.CreateTranslation(-.6f, 0, 0), Matrix4x4.CreateTranslation(-1.2f, 0, 0)], [1, 2]);
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                for (int actor = 0; actor < 2; actor++)
                {
                    targets[actor] = GpuPoseAnimation.CreateTarget(frame.World, bodies[actor]);
                    using var builder = new AnimationBuilder(frame.World);
                    builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(Entity.Null,
                        new(pose, Duration: actor == 0 ? float.PositiveInfinity : .3f));
                    graphs[actor] = builder.Compile();
                    animations[actor] = graphs[actor].Play(frame.World, targets[actor]);
                }
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0 ? completed : stopped, binding));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 3) PropertyAnimation.Stop(frame.World, animations[0]);
                if (frame.Index == 5) animations[0] = graphs[0].Play(frame.World, targets[0]);
            },
        };
        SceneAction[] actions = Enumerable.Range(0, 6).Select(index => new SceneAction(index, edit =>
        {
            foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add);
        })).ToArray();
        try
        {
            await SceneImages.RunAsync("d3d12", new SceneScript(application, 6, actions), "--fixed-step", "0.1", "--no-taa");
        }
        finally { foreach (CompiledGraph graph in graphs) graph.Dispose(); }

        Assert.Equal(12, reads.Count);
        for (int actor = 0; actor < 2; actor++)
        {
            GpuPoseReadback held = reads[4 + actor];
            Assert.True(Vector3.Distance(reads[actor].Current.Span[2].Translation, held.Current.Span[2].Translation) > .001f);
            for (int frame = 3; frame <= 4; frame++)
                for (int bone = 0; bone < skeleton.BoneCount; bone++)
                {
                    GpuPoseExamples.Near(held.Current.Span[bone], reads[frame * 2 + actor].Current.Span[bone]);
                    GpuPoseExamples.Near(held.Current.Span[bone], reads[frame * 2 + actor].Previous.Span[bone]);
                }
        }
        Assert.Equal(reads[0].Identity.AnimationStarts + 1, reads[10].Identity.AnimationStarts);
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
        {
            GpuPoseExamples.Near(reads[0].Current.Span[bone], reads[10].Current.Span[bone]);
            GpuPoseExamples.Near(reads[5].Current.Span[bone], reads[11].Current.Span[bone]);
        }
    }
}
