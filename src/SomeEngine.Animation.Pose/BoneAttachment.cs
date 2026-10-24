using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Animation.Pose;

/// <summary>A selected live bone and its attachment offset, relative to the entity's actor parent.</summary>
public struct BoneAttachment : IComponent
{
    public readonly SkeletonInstance Skeleton;
    public readonly BonePose Bone;
    public TransformQvvs Offset;

    public BoneAttachment(SkeletonInstance skeleton, int bone, TransformQvvs offset)
    {
        Skeleton = skeleton;
        Bone = skeleton.ModelPose.GetBone(bone);
        Offset = offset;
    }

    internal readonly TransformQvvs ReadLocalTransform()
    {
        var local = Offset.ToMatrix() * Bone.Transform;
        if (TransformQvvs.TryCreateFromMatrix(local, out TransformQvvs transform)) return transform;
        throw new InvalidOperationException("Bone attachments require a local matrix representable by QVVS.");
    }
}

public static class BoneAttachments
{
    /// <summary>Binds a requested entity to a live bone and installs its actor parent and transform components.</summary>
    public static void Bind(World world, Entity attachment, Entity actor,
        SkeletonInstance skeleton, int bone, TransformQvvs offset)
    {
        world.Add(attachment, new BoneAttachment(skeleton, bone, offset));
        if (!world.Has<LocalTransform>(attachment))
        {
            world.Add(attachment, new LocalTransform { Value = TransformQvvs.Identity });
            world.Add(attachment, new WorldTransform { Qvvs = TransformQvvs.Identity });
        }
        Hierarchy.SetParent(world, attachment, actor);
    }

    /// <summary>Selects the source bone used by the next attachment update.</summary>
    public static void SelectBone(World world, Entity attachment, int bone)
    {
        BoneAttachment current = world.Read<BoneAttachment>(attachment);
        world.Replace(attachment, new BoneAttachment(current.Skeleton, bone, current.Offset));
    }
}
