using System.Numerics;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class SkeletonInstanceBehaviorTests
{
    [Fact]
    public void ReferencePoseProducesModelTransformsThroughTheParentHierarchy()
    {
        var skeleton = new Skeleton([-1, 0, 1],
            [new() { Translation = new(10, 20, 30), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Scale = new(2) },
             new() { Translation = new(3, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = new(0, 4, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [0, 0, 0]);
        var poses = new PoseStorage();
        using SkeletonInstance instance = poses.CreateInstance(skeleton);

        ModelPose pose = instance.ModelPose;

        Assert.InRange(Vector3.Distance(new(10, 20, 30), pose.Transforms[0].Translation), 0, 0.0001f);
        Assert.InRange(Vector3.Distance(new(10, 26, 30), pose.Transforms[1].Translation), 0, 0.0001f);
        Assert.InRange(Vector3.Distance(new(2, 26, 30), pose.Transforms[2].Translation), 0, 0.0001f);
        Assert.InRange(Vector3.Distance(new(2, 28, 30), Vector3.Transform(Vector3.UnitX, pose.Transforms[2])), 0, 0.0001f);
    }

    [Fact]
    public void LocalPoseUpdatePublishesModelTransformsWithItsTimeAndNextVersion()
    {
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [0, 0]);
        var poses = new PoseStorage();
        using SkeletonInstance instance = poses.CreateInstance(skeleton);
        ulong previousVersion = instance.ModelPose.Version;
        BoneTransform[] local = skeleton.ReferencePose.ToArray();
        local[0].Translation = new(5, 0, 0);
        local[1].Translation = new(0, 4, 0);

        instance.Update(local, 1.25);

        ModelPose pose = instance.ModelPose;
        Assert.Equal(new Vector3(5, 4, 0), pose.Transforms[1].Translation);
        Assert.Equal(previousVersion + 1, pose.Version);
        Assert.Equal(1.25, pose.Time);
        Assert.Equal(new Vector3(0, 4, 0), instance.LocalPose[1].Translation);
    }

    [Fact]
    public void ReleasedInstancesCanBeRecreatedWhileOtherInstancesKeepTheirOwnPose()
    {
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [0, 0]);
        var poses = new PoseStorage();
        SkeletonInstance first = poses.CreateInstance(skeleton);
        using SkeletonInstance second = poses.CreateInstance(skeleton);
        BoneTransform[] local = skeleton.ReferencePose.ToArray();
        local[0].Translation = new(2, 0, 0);
        first.Update(local, 1);
        local[0].Translation = new(9, 0, 0);
        second.Update(local, 2);

        first.Dispose();
        using SkeletonInstance recreated = poses.CreateInstance(skeleton);

        Assert.Same(skeleton, recreated.Skeleton);
        Assert.Equal(Vector3.UnitY, recreated.ModelPose.Transforms[1].Translation);
        Assert.Equal(1ul, recreated.ModelPose.Version);
        Assert.Equal(0, recreated.ModelPose.Time);
        Assert.Equal(new Vector3(9, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(2ul, second.ModelPose.Version);
        Assert.Equal(2, second.ModelPose.Time);
    }
}
