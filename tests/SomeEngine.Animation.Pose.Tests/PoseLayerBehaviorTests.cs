using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PoseLayerBehaviorTests
{
    private static readonly Channel<float> UpperWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);

    [Fact]
    public async Task MaskedLayersAndBlendsUseThisFramesAnimatedWeight()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pose-mask-{Guid.NewGuid():N}.bone-mask.asset");
        BoneMask upper;
        try
        {
            using (var asset = new BoneMaskAsset())
            using (var block = new BoneMaskBlock([0, 0.5f, 0]))
            {
                asset.SetBlock(block);
                await asset.SaveAsync(path);
            }
            using BoneMaskAsset reopened = await BoneMaskAsset.OpenAsync(path);
            upper = (await reopened.GetBlockAsync<BoneMaskBlock>()).CreateMask();
        }
        finally { File.Delete(path); }

        using var world = new World();
        var skeleton = new Skeleton([-1, 0, 1],
            [Transform(Vector3.Zero, Quaternion.Identity, 1),
             Transform(Vector3.UnitY, new(0, 0, 0.70710677f, 0.70710677f), 2),
             Transform(Vector3.UnitY, Quaternion.Identity, 1)], [1, 2, 3]);
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        var target = PoseAnimation.CreateTarget(world, instance, resultCount: 6, output: new PoseResultId(5));
        var layers = new AnimationLayers { Count = 3 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 0 };
        layers.Items[2] = new() { Mode = LayerMode.Additive, Weight = 0.5f };
        world.Add(target, layers);
        var weight = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.To(world, target, UpperWeight, 1f, weight);

        AnimationClip basis = CreateClip(skeleton,
            [Transform(new(4, 0, 0), Quaternion.Identity, 1),
             Transform(new(0, 2, 0), Quaternion.Identity, 1),
             Transform(Vector3.UnitY, Quaternion.Identity, 1)]);
        AnimationClip cover = CreateClip(skeleton,
            [Transform(new(100, 0, 0), Quaternion.Identity, 1),
             Transform(new(0, 6, 0), new(0, 0, 1, 0), 3),
             Transform(new(20, 20, 0), Quaternion.Identity, 1)]);
        AnimationClip additive = CreateClip(skeleton,
            [Transform(new(200, 0, 0), Quaternion.Identity, 1),
             Transform(new(0, 5, 0), new(0, 0, 1, 0), 6),
             Transform(new(30, 30, 0), Quaternion.Identity, 1)]);
        var between = new PoseBlend([new(new PoseResultId(3)), new(new PoseResultId(0))]);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.LayerPose(target, new PoseLayerParameters(new(4), new(2), new(5), new BoneMask([0, 1, 0])), layer: 2);
            builder.BlendPose(target, new(between, new(4), new(StageEnd.Settled)));
            builder.LayerPose(target, new PoseLayerParameters(new(0), new(1), new(3), upper), layer: 1);
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(basis, new(0), Loop: true));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(cover, new(1), Loop: true));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(additive, new(2), Loop: true));
        }
        builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, weight);
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new PoseLayerSystem());
            nodes.Add(new PoseBlendSystem());
        });

        Tick(systems, world, 0.5);
        Assert.Equal(0.5f, world.Read<AnimationLayers>(target).Items[1].Weight);
        Near(new(4, 0, 0), instance.LocalPose[0].Translation);
        Near(new(0, 4.5f, 0), instance.LocalPose[1].Translation);
        Near(new(2.5f), instance.LocalPose[1].Scale);
        Near(new(0.4472136f, 0.8944272f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[1].Rotation));
        Near(Vector3.UnitY, instance.LocalPose[2].Translation);
        Near(new(1.763932f, 5.618034f, 0), instance.ModelPose.Transforms[2].Translation);

        Tick(systems, world, 0.5);
        Assert.Equal(1f, world.Read<AnimationLayers>(target).Items[1].Weight);
        Near(new(0, 5, 0), instance.LocalPose[1].Translation);
        Near(new(3), instance.LocalPose[1].Scale);
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, instance.LocalPose[1].Rotation));
        Near(new(1, 5, 0), instance.ModelPose.Transforms[2].Translation);
    }

    private static BoneTransform Transform(Vector3 translation, Quaternion rotation, float scale) =>
        new() { Translation = translation, Rotation = rotation, Scale = new(scale) };

    private static AnimationClip CreateClip(Skeleton skeleton, ReadOnlySpan<BoneTransform> values) =>
        new(skeleton, 2, ClipSampler.Create<PoseData, ConstantPoseSampler>(new(values.ToArray())));

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private sealed record PoseData(BoneTransform[] Values);
    private readonly struct ConstantPoseSampler : IClipSampler<PoseData>
    {
        public static void SampleLocal(PoseData data, float time, Span<BoneTransform> destination) =>
            data.Values.CopyTo(destination);
    }
}
