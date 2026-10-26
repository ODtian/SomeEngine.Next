using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
                GpuPoseTarget input = frame.World.Read<GpuPoseTarget>(target);
                input.ModelToWorld = Matrix4x4.CreateScale(1, 2, 1); frame.World.Replace(target, input);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(instance, binding));
            },
            UpdateFrame = frame => parent.Update([Bone(0, frame.Index == 0 ? 0 : .2f, new(2, 1, 1))], (frame.Index + 1) * .1),
            FrameCompleted = frame =>
            {
                if (frame.Index != 1) return;
                QueryHandle members = frame.World.Query(new QueryDefinitionBuilder().Read<GpuPoseBody>().Read<DeviceBodyBinding>());
                var entities = new List<Entity>();
                frame.World.ExecuteReadQuery(members, ref entities, (scoped QueryCursor rows, ref List<Entity> entities) =>
                { foreach (QueryRow row in rows.Rows) if (row.Read<GpuPoseBody>().Target == target) entities.Add(row.Entity); });
                Assert.Equal(2, entities.Count);
                Entity fixedEnd = entities.Single(entity => frame.World.Read<GpuPoseBody>(entity).Bone == 0);
                Entity moving = entities.Single(entity => frame.World.Read<GpuPoseBody>(entity).Bone == 1);
                DeviceBodyBinding selected = frame.World.Read<DeviceBodyBinding>(moving);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, members, 2);
                PhysicsSimulation.Create(frame.World, QueryDefinition.Empty, results.Readback(64), 1).Schedule(1, .1, .1).Complete();
                anchor = frame.World.Read<BodyPose>(fixedEnd).Position;
                position = frame.World.Read<BodyPose>(moving).Position;
                Assert.Equal(.2, frame.World.Read<BodyStepState>(moving).Time, 8);
                frame.World.ReleaseQuery(members);
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
