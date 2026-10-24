using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class BoneAttachmentBehaviorTests
{
    [Fact]
    public void RequestedAttachmentsFollowTheSelectedBoneAndRetainTheirWorldPoseWhenTheSkeletonIsReleased()
    {
        using var game = new GameWorld();
        World world = game.World;
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = new(0, 2, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity poseTarget = PoseAnimation.CreateTarget(world, instance);
        Entity actor = world.CreateEntity();
        world.Add(actor, new LocalTransform { Value = new(new(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), 2) });
        world.Add(actor, new WorldTransform { Qvvs = TransformQvvs.Identity });
        Entity weapon = world.CreateEntity();
        Entity ornament = world.CreateEntity();
        var offset = new TransformQvvs(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), .5f);
        BoneAttachments.Bind(world, weapon, actor, instance, bone: 1, offset);
        BoneAttachments.Bind(world, ornament, actor, instance, bone: 1, TransformQvvs.Identity);
        var raw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 0), new(2, 0, 0, 0)]),
            new(1, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
        ]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw));
        using var builder = new AnimationBuilder(world);
        builder.Leaf<ClipParameters, ClipLeaf>(poseTarget, new(clip, new PoseResultId(0)));
        Entity animation = builder.Play();
        PoseAnimation.AddSystems(game.Systems);
        AnimationSystems.Add(game.Systems, new BoneAttachmentSystem(), AnimationPhase.Composition);

        game.Update(.5);
        Matrix4x4 first = Matrix4x4.CreateRotationZ(5 * MathF.PI / 4)
            * Matrix4x4.CreateTranslation(4.5857863f, 3.4142137f, 0);
        Near(first, world.Read<WorldTransform>(weapon).Qvvs.ToMatrix());
        Assert.Equal(actor, Hierarchy.GetParent(world, weapon));

        BoneAttachments.SelectBone(world, weapon, bone: 0);
        world.Replace(actor, new LocalTransform { Value = new(new(20, 5, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI), 2) });
        game.Update(.25);
        Matrix4x4 switched = Matrix4x4.CreateRotationZ(3 * MathF.PI / 2) * Matrix4x4.CreateTranslation(15, 5, 0);
        Near(switched, world.Read<WorldTransform>(weapon).Qvvs.ToMatrix());

        world.DestroyEntity(weapon);
        game.Update(.25);
        Assert.False(world.IsAlive(weapon));
        Assert.False(instance.IsDisposed);
        Matrix4x4 held = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationZ(3 * MathF.PI / 2)
            * Matrix4x4.CreateTranslation(16, 1, 0);
        Near(held, world.Read<WorldTransform>(ornament).Qvvs.ToMatrix());

        PropertyAnimation.Stop(world, animation);
        world.DestroyEntity(poseTarget);
        instance.Dispose();
        world.Replace(actor, new LocalTransform { Value = new(new(100, 5, 0), Quaternion.Identity, 2) });
        game.Update(0);
        Assert.True(world.IsAlive(ornament));
        Assert.False(world.Has<BoneAttachment>(ornament));
        Assert.Equal(Entity.Null, Hierarchy.GetParent(world, ornament));
        Near(held, world.Read<LocalTransform>(ornament).Value.ToMatrix());
        Near(held, world.Read<WorldTransform>(ornament).Qvvs.ToMatrix());
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        foreach (Vector3 point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.InRange(Vector3.Distance(Vector3.Transform(point, expected), Vector3.Transform(point, actual)), 0, .0001f);
    }
}
