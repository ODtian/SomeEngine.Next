using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class SkeletonInheritanceBehaviorTests
{
    [Fact]
    public void StandaloneAndAttachedClothingShareTheSameDefinitionAndBindingResults()
    {
        var body = new Skeleton([-1, 0], [At(1, 0, 0), At(0, 2, 0)], [10, 20]);
        var clothing = new Skeleton(body, [-1], [At(0, 0, 3)], [30], inheritedParents: [1]);
        var binding = new SkinBinding(
            [Matrix4x4.CreateTranslation(-1, -2, 0), Matrix4x4.CreateTranslation(-1, -2, -3)], [1, 2]);
        var poses = new PoseStorage();
        using SkeletonInstance character = poses.CreateInstance(body);
        using SkeletonInstance standalone = poses.CreateInstance(clothing);
        using SkeletonInstance attached = poses.CreateInstance(clothing, character);
        SkinBindingInstance standaloneSkin = binding.Bind(standalone);
        SkinBindingInstance attachedSkin = binding.Bind(attached);

        Assert.Same(body, clothing.Parent);
        Assert.Equal(1, clothing.BoneCount);
        Assert.Equal(3, clothing.TotalBoneCount);
        Assert.Same(body, standalone.ParentInstance!.Skeleton);
        Assert.Same(character, attached.ParentInstance);
        Vector3[] expected = [new(1, 0, 0), new(1, 2, 0), new(1, 2, 3)];
        for (int bone = 0; bone < expected.Length; bone++)
        {
            Assert.Equal(expected[bone], standalone.ModelPose.Transforms[bone].Translation);
            Assert.Equal(expected[bone], attached.ModelPose.Transforms[bone].Translation);
        }
        for (int joint = 0; joint < 2; joint++)
        {
            Assert.Equal(Matrix4x4.Identity, standaloneSkin.GetSkinTransform(joint));
            Assert.Equal(Matrix4x4.Identity, attachedSkin.GetSkinTransform(joint));
        }
    }

    [Fact]
    public void ReconnectionKeepsLiveInheritedResultsAndReleasesThePrivateParentChain()
    {
        var body = new Skeleton([-1, 0], [At(1, 0, 0), At(0, 2, 0)], [10, 20]);
        var clothing = new Skeleton(body, [-1], [At(0, 0, 3)], [30], inheritedParents: [1]);
        var ornament = new Skeleton(clothing, [-1], [At(1, 0, 0)], [40], inheritedParents: [2]);
        var binding = new SkinBinding(
            [Matrix4x4.CreateTranslation(-1, -2, 0), Matrix4x4.CreateTranslation(-1, -2, -3)], [1, 2]);
        var poses = new PoseStorage();
        using SkeletonInstance character = poses.CreateInstance(body);
        using SkeletonInstance garment = poses.CreateInstance(clothing);
        using SkeletonInstance decoration = poses.CreateInstance(ornament, garment);
        SkeletonInstance originalParent = garment.ParentInstance!;
        ModelPose view = garment.ModelPose;
        BonePose inherited = view.GetBone(1);
        SkinBindingInstance skin = binding.Bind(garment);
        character.Update([At(5, 0, 0), At(0, 2, 0)], 1.25);

        garment.Attach(character, time: 1.25);

        Assert.True(originalParent.IsDisposed);
        Assert.Same(character, inherited.Instance);
        Assert.Same(character, skin.GetJointPose(0).Instance);
        Assert.Equal(new Vector3(5, 2, 3), view.Transforms[2].Translation);
        Assert.Equal(new Vector3(6, 2, 3), decoration.ModelPose.Transforms[3].Translation);
        character.Update([At(9, 0, 0), At(0, 2, 0)], 2.5);
        Assert.Equal(new Vector3(9, 2, 0), inherited.Transform.Translation);
        Assert.Equal(character.ModelPose.Version, inherited.Version);
        Assert.Equal(2.5, inherited.Time);
        Assert.Equal(2, view.SectionCount);
        Assert.Same(character, view.GetSection(0).Instance);
        Assert.Equal(2.5, view.GetSection(0).Time);
        Assert.Equal(1.25, view.GetSection(1).Time);
        Assert.Equal(2, view.GetSection(1).BoneStart);

        garment.Update([At(0, 0, 4)], 2.5);
        decoration.Update(decoration.LocalPose, 2.5);

        Assert.Equal(new Vector3(9, 2, 4), view.Transforms[2].Translation);
        Assert.Equal(new Vector3(8, 0, 0), skin.GetSkinTransform(0).Translation);
        Assert.Equal(new Vector3(8, 0, 1), skin.GetSkinTransform(1).Translation);
        garment.Detach(time: 3);
        SkeletonInstance detachedParent = garment.ParentInstance!;
        character.Update([At(20, 0, 0), At(0, 2, 0)], 4);
        Assert.Same(detachedParent, inherited.Instance);
        Assert.Same(detachedParent, skin.GetJointPose(0).Instance);
        Assert.Same(detachedParent, decoration.ModelPose.GetBone(1).Instance);
        Assert.Equal(new Vector3(9, 2, 0), inherited.Transform.Translation);
        Assert.Equal(new Vector3(10, 2, 4), decoration.ModelPose.Transforms[3].Translation);
        Matrix4x4[] copied = new Matrix4x4[3];
        view.Transforms.CopyTo(copied);
        Assert.Equal(new Vector3(9, 2, 4), copied[2].Translation);

        decoration.Dispose();
        garment.Dispose();

        Assert.True(detachedParent.IsDisposed);
        Assert.False(character.IsDisposed);
        Assert.Equal(new Vector3(20, 2, 0), character.ModelPose.Transforms[1].Translation);
    }

    [Fact]
    public void ModelSamplingUsesTheBoundParentResultWithOwnBoneSelections()
    {
        var body = new Skeleton([-1, 0], [At(1, 0, 0), At(0, 2, 0)], [10, 20]);
        var clothing = new Skeleton(body, [-1, 0], [At(0, 0, 3), At(0, 1, 0)], [30, 40],
            inheritedParents: [1, -1]);
        var data = new RawClipData(1,
            [new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 3, 0), new(0, 0, 7, 0)])]);
        var clip = new AnimationClip(clothing, 1, ClipSampler.Create<RawClipData, RawClipSampler>(data));
        var poses = new PoseStorage();
        using SkeletonInstance first = poses.CreateInstance(body);
        using SkeletonInstance second = poses.CreateInstance(body);
        first.Update([At(5, 0, 0), At(0, 2, 0)], .5);
        second.Update([At(9, 0, 0), At(0, 2, 0)], 1);
        var batch = new ClipSampleBatch(
            [new(clip, [1], parentPose: first.ModelPose), new(clip, [1], parentPose: second.ModelPose)]);
        BoneTransform[] local = new BoneTransform[batch.OutputCount];
        Matrix4x4[] model = new Matrix4x4[batch.OutputCount];

        batch.SampleLocal([.5f, 1], local);
        batch.SampleModel([.5f, 1], model);

        Assert.Equal(new Vector3(0, 1, 0), local[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), local[1].Translation);
        Assert.Equal(new Vector3(5, 3, 5), model[0].Translation);
        Assert.Equal(new Vector3(9, 3, 7), model[1].Translation);
        first.Update([At(13, 0, 0), At(0, 2, 0)], 2);
        batch.SampleModel([.5f, 1], model);
        Assert.Equal(new Vector3(13, 3, 5), model[0].Translation);
        Assert.Equal(new Vector3(9, 3, 7), model[1].Translation);
    }

    [Fact]
    public void ParentAnimationRecomputesHeldChildResultsOnTheCommonCalculationClock()
    {
        using var world = new World();
        var body = new Skeleton([-1, 0], [At(1, 0, 0), At(0, 2, 0)], [10, 20]);
        var clothing = new Skeleton(body, [-1], [At(0, 0, 3)], [30], inheritedParents: [1]);
        var poses = new PoseStorage();
        using SkeletonInstance character = poses.CreateInstance(body);
        using SkeletonInstance garment = poses.CreateInstance(clothing, character);
        Entity garmentTarget = PoseAnimation.CreateTarget(world, garment);
        Entity characterTarget = PoseAnimation.CreateTarget(world, character);
        PoseResultReference garmentOutput = PoseAnimation.Resolve(world, garmentTarget, default);
        PoseResultReference characterOutput = PoseAnimation.Resolve(world, characterTarget, default);
        var clothingData = new RawClipData(.5f,
            [new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, .5f],
                [new(0, 0, 3, 0), new(0, 0, 4, 0)])]);
        var bodyData = new RawClipData(1,
            [new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(1, 0, 0, 0), new(9, 0, 0, 0)])]);
        using var clothingBuilder = new AnimationBuilder(world);
        clothingBuilder.Leaf<ClipParameters, ClipLeaf>(garmentTarget,
            new(new AnimationClip(clothing, .5f, ClipSampler.Create<RawClipData, RawClipSampler>(clothingData)), default));
        clothingBuilder.Play();
        using var bodyBuilder = new AnimationBuilder(world);
        bodyBuilder.Leaf<ClipParameters, ClipLeaf>(characterTarget,
            new(new AnimationClip(body, 1, ClipSampler.Create<RawClipData, RawClipSampler>(bodyData)), default));
        Entity animation = bodyBuilder.Play();
        PropertyAnimation.SetSpeed(world, animation, .5f);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);
        var frame = new ImmediateSystemContext(world, 0, 0, .5);

        systems.Update(ref frame);

        Assert.Equal(new Vector3(3, 2, 4), garment.ModelPose.Transforms[2].Translation);
        Assert.Equal(.5, character.ModelPose.Time);
        Assert.Equal(.5, garment.ModelPose.Time);
        Assert.Equal(.25, character.ModelPose.SampleTime);
        Assert.Equal(.5, garment.ModelPose.SampleTime);
        Assert.Equal(.25, PoseAnimation.ReadResult(world, in characterOutput).Time);
        Assert.Equal(.5, PoseAnimation.ReadResult(world, in garmentOutput).Time);
        ulong heldLocal = PoseAnimation.ReadResult(world, in garmentOutput).Version;
        ulong firstModel = garment.ModelPose.Version;

        systems.Update(ref frame);

        Assert.Equal(new Vector3(5, 2, 4), garment.ModelPose.Transforms[2].Translation);
        Assert.Equal(heldLocal, PoseAnimation.ReadResult(world, in garmentOutput).Version);
        Assert.True(garment.ModelPose.Version > firstModel);
        Assert.Equal(1, garment.ModelPose.GetSection(0).Time);
        Assert.Equal(1, garment.ModelPose.GetSection(1).Time);
        Assert.Equal(.5, garment.ModelPose.GetSection(1).SampleTime);
        Assert.Equal(.5, PoseAnimation.ReadResult(world, in garmentOutput).Time);
    }

    private static BoneTransform At(float x, float y, float z) => new()
    {
        Translation = new(x, y, z), Rotation = Quaternion.Identity, Scale = Vector3.One,
    };
}
