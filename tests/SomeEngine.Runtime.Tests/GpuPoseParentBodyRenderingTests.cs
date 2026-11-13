using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Physics;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task TheSecondaryChainInheritsItsCpuParentScaleAndKeepsWorldInertiaUnderActorScale()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(float x, float y = 0, Vector3? scale = null) => new()
            { Translation = new(x, y, 0), Rotation = Quaternion.Identity, Scale = scale ?? Vector3.One };
        var parentSkeleton = new Skeleton([-1], [Bone(0, scale: new(2, 1, 1))], [1]);
        var skeleton = new Skeleton(parentSkeleton, [-1, 0], [Bone(0), Bone(1)], [2, 3], [0, -1]);
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentSkeleton);
        using SkeletonInstance instance = storage.CreateInstance(skeleton, parent);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        var pose = new GpuXpbdPose(blend, new(skeleton, [new(0), new(1)], new(0, -6, 0), damping: 0, step: .1f, iterations: 1));
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0)], [1, 2]);
        Entity target = default;
        CompiledGraph? graph = null;
        var hardware = new PoseBodyHardware();
        var reads = new List<GpuPoseReadback>();
        Vector3 anchor = default, position = default;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                target = GpuPoseAnimation.CreateTarget(frame.World, instance);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(Entity.Null, new(pose));
                CompiledGraph compiled = builder.Compile(); graph = compiled; compiled.Play(frame.World, target);
                GpuPoseTarget input = frame.World.ReadComponentData<GpuPoseTarget>(target);
                input.ModelToWorld = Matrix4x4.CreateScale(1, 2, 1); frame.World.Set(target, input);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(instance, binding));
            },
            UpdateFrame = frame => parent.Update([Bone(0, frame.Index == 0 ? 0 : .2f, new(2, 1, 1))], (frame.Index + 1) * .1),
            FrameCompleted = frame =>
            {
                if (frame.Index != 1) return;
                Query members = frame.World.Query(new QueryDescription().WithStored<GpuPoseBody>().WithStored<DeviceBodyBinding>());
                var entities = new List<Entity>();
                using (QueryScope rows = members.OpenRead())
                    foreach (QueryChunk chunk in rows)
                    {
                        ReadOnlySpan<GpuPoseBody> metadata = chunk.Read<GpuPoseBody>();
                        for (int row = 0; row < chunk.Count; row++)
                            if (metadata[row].Target == target) entities.Add(chunk.Entities[row]);
                    }
                Assert.Equal(2, entities.Count);
                Entity fixedEnd = entities.Single(entity => frame.World.ReadComponentData<GpuPoseBody>(entity).Bone == 0);
                Entity moving = entities.Single(entity => frame.World.ReadComponentData<GpuPoseBody>(entity).Bone == 1);
                DeviceBodyBinding selected = frame.World.ReadComponentData<DeviceBodyBinding>(moving);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, members, 2);
                PhysicsSimulation.Create(frame.World, world.Query(new QueryDescription()), results.Readback(64), 1).Schedule(1, .1, .1).Complete();
                anchor = frame.World.ReadComponentData<BodyPose>(fixedEnd).Position;
                position = frame.World.ReadComponentData<BodyPose>(moving).Position;
                Assert.Equal(.2, frame.World.ReadComponentData<BodyStepState>(moving).Time, 8);
                
            },
        };
        application.InstallGeometry(hardware);
        try
        {
            await SceneImages.RunAsync("d3d12", new SceneScript(application, 2,
                [new(1, edit => edit.ReadGpuPose(target, reads.Add))]), "--fixed-step", "0.1", "--no-taa");
        }
        finally { graph?.Dispose(); }

        // The anchor reaches .4 world Y; the gravity prediction (2,-.06) projects to its radius-two circle.
        Assert.InRange(Vector3.Distance(new(0, .4f, 0), anchor), 0, .001f);
        Assert.InRange(Vector3.Distance(new(1.9491104f, -.04829539f, 0), position), 0, .001f);
        Assert.InRange(Vector3.Distance(new(1.9491104f, -.02414769f, 0), Assert.Single(reads).Current.Span[1].Translation), 0, .001f);
    }
}
