using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task GpuSecondaryChainsFollowActorAndParentMotionWithTheirOwnConstrainedHistoryAndRepeatedInput()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        // These two short tails are examples of the general configured chain.
        var skeleton = new Skeleton([-1, 0, 1, 2, 3],
            [Bone(Vector3.Zero), Bone(Vector3.Zero), Bone(new(.6f, -.15f, 0)),
             Bone(new(.55f, -.2f, .1f)), Bone(new(.45f, -.2f, .05f))], [1, 2, 3, 4, 5]);
        var raw = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(QuantizedClipData.Encode(raw, 4), Loop: true));
        GpuXpbdChain[] chains =
        [
            new(skeleton, [new(1), new(2), new(3)], new(0, -6, 1), maximumBend: .35f, step: .05f, iterations: 24),
            new(skeleton, [new(1), new(2), new(3, .75f), new(4, .5f)], new(0, -6, 1),
                lengthCompliance: .0005f, bendCompliance: .0002f, maximumBend: .55f, damping: .5f, step: .05f, iterations: 24),
        ];
        Vector3[][][] expected = ReferencePoints();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [first, second];
        Entity[] targets = new Entity[2], animations = new Entity[2];
        CompiledGraph[] graphs = new CompiledGraph[2];
        var repeat = new GpuPoseExamples.RepeatLastInput(targets);
        var reads = new List<GpuPoseReadback>();
        var binding = new SkinBinding([Matrix4x4.CreateTranslation(-.6f, .15f, 0), Matrix4x4.CreateTranslation(-1.15f, .35f, -.1f)], [2, 3]);
        SceneImage? actual = null;
        var submitted = new List<SceneLinearImage>();
        var shadowSettings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
        };
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, repeat, AnimationPhase.Completion);
                for (int actor = 0; actor < 2; actor++)
                {
                    targets[actor] = GpuPoseAnimation.CreateTarget(frame.World, bodies[actor]);
                    using var builder = new AnimationBuilder(frame.World);
                    builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(Entity.Null, new(new(source, chains[actor]), TranslationBone: 0));
                    graphs[actor] = builder.Compile();
                    animations[actor] = graphs[actor].Play(frame.World, targets[actor]);
                }
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0 ? second : first, binding));
            },
            UpdateFrame = frame =>
            {
                repeat.Repeat = frame.Index == 3;
                if (frame.Index == 4)
                    for (int actor = 0; actor < 2; actor++)
                    {
                        PropertyAnimation.Stop(frame.World, animations[actor]);
                        animations[actor] = graphs[actor].Play(frame.World, targets[actor]);
                    }
                (float yaw, Vector3 position, float parent) = frame.Index switch
                {
                    0 => (0f, Vector3.Zero, 0f),
                    1 => (.12f, new Vector3(.12f, .06f, 0), .05f),
                    5 => (.3f, new Vector3(.32f, .12f, .05f), .15f),
                    _ => (.24f, new Vector3(.24f, .08f, .03f), .1f),
                };
                for (int actor = 0; actor < 2; actor++)
                {
                    GpuPoseTarget input = frame.World.Read<GpuPoseTarget>(targets[actor]);
                    input.ModelToWorld = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
                    input.ModelOffset = new(0, parent, 0);
                    frame.World.Replace(targets[actor], input);
                }
            },
        };
        SceneAction[] actions = Enumerable.Range(0, 6).Select(index => new SceneAction(index, edit =>
        {
            foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add);
            if (index is 0 or 5) edit.CaptureLinearImage(submitted.Add);
            if (index == 5) edit.CaptureImage(image => actual = image);
        })).ToArray();
        try
        {
            await SceneImages.RunAsync("d3d12", new SceneScript(application, 6, actions) { ShadowSettings = shadowSettings },
                "--fixed-step", "0.1", "--no-taa");
        }
        finally { foreach (CompiledGraph graph in graphs) graph.Dispose(); }
        Assert.Equal(12, reads.Count);
        int[] priorFrames = [0, 0, 1, 1, 2, 4];
        for (int frame = 0; frame < 6; frame++)
        for (int actor = 0; actor < 2; actor++)
        {
            GpuPoseReadback result = reads[frame * 2 + actor];
            Assert.Same(bodies[actor], result.Identity.Instance);
            for (int point = 0; point < chains[actor].Bones.Length; point++)
            {
                int bone = chains[actor].Bones[point].Bone;
                Assert.True(Vector3.Distance(expected[actor][frame][point], result.Current.Span[bone].Translation) <= .001f,
                    $"frame {frame}, actor {actor}, point {point}: expected {expected[actor][frame][point]}, actual {result.Current.Span[bone].Translation}");
                Assert.InRange(Vector3.Distance(expected[actor][priorFrames[frame]][point], result.Previous.Span[bone].Translation), 0, .001f);
            }
            if (actor == 0)
            {
                Vector3 a = result.Current.Span[1].Translation, b = result.Current.Span[2].Translation, c = result.Current.Span[3].Translation;
                Assert.InRange(MathF.Abs(Vector3.Distance(a, b) - MathF.Sqrt(.3825f)), 0, .001f);
                Assert.InRange(MathF.Abs(Vector3.Distance(b, c) - MathF.Sqrt(.3525f)), 0, .001f);
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(b - a), Vector3.Normalize(c - b)), -1, 1));
                Assert.InRange(angle, 0, chains[actor].MaximumBend + .001f);
            }
        }
        for (int actor = 0; actor < 2; actor++)
        {
            GpuPoseReadback before = reads[4 + actor], held = reads[6 + actor], restarted = reads[8 + actor];
            Assert.Equal(before.Identity, held.Identity);
            Assert.Equal(before.CalculationTime, held.CalculationTime);
            Assert.Equal(before.Identity.AnimationStarts + 1, restarted.Identity.AnimationStarts);
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
            {
                GpuPoseExamples.Near(before.Current.Span[bone], held.Current.Span[bone]);
                GpuPoseExamples.Near(before.Previous.Span[bone], held.Previous.Span[bone]);
            }
        }
        Assert.Equal(2, submitted.Count);
        VirtualShadowFrameQuality[] shadows = submitted.Select(image =>
            Assert.IsType<VirtualShadowFrameQuality>(Assert.IsType<SceneRenderingQuality>(image.Frame.Quality).Shadows)).ToArray();
        Assert.All(shadows, shadow => Assert.True(shadow.Views.Length > 1, $"Submitted shadow views: {shadow.Views.Length}"));
        Assert.Contains(shadows.SelectMany(shadow => shadow.Views), view => view.HardwareVisibleItems + view.SoftwareVisibleItems > 0);
        await content.WriteReferenceMeshesAsync((previous, right) =>
        {
            Vector3[] points = expected[right ? 1 : 0][previous ? 4 : 5];
            // Direct source-frame swing is the declared twist convention; these positions come from the independent numerical reference.
            Matrix4x4 middle = At(Swing(new(.55f, -.2f, .1f), points[2] - points[1]), points[1]);
            Matrix4x4 tip = At(Swing(new(.55f, -.2f, .1f), points[2] - points[1]), points[2]);
            if (right) tip = At(Swing(new(.45f, -.2f, .05f), points[3] - points[2]), points[2]);
            return (binding.InverseBindMatrices[0] * middle, binding.InverseBindMatrices[1] * tip);
        });
        await content.SetReferenceShaderAsync();
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(new ApplicationScene(content.Project.RootPath), 6,
            [new(5, edit => edit.CaptureImage(image => reference = image))]) { ShadowSettings = shadowSettings },
            "--fixed-step", "0.1", "--no-taa");
        SceneImage image = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in image.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000, $"Visible reference channels: {visible}");
        SceneImages.AssertEqualWithinPixel(image, Assert.IsType<SceneImage>(actual), tolerance: 2);

        static Matrix4x4 At(Matrix4x4 orientation, Vector3 position) { orientation.Translation = position; return orientation; }
        static Matrix4x4 Swing(Vector3 source, Vector3 target)
        {
            source = Vector3.Normalize(source); target = Vector3.Normalize(target);
            return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new(Vector3.Cross(source, target), 1 + Vector3.Dot(source, target))));
        }
    }

}
