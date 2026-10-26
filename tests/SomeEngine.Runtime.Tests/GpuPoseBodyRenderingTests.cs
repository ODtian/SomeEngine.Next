using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Physics;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task TheOriginalSecondaryChainLeafProducesEntityBodiesAndExtractsTheirCompletedBones()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(float x) => new() { Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(0), Bone(1)], [1, 2]);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        var pose = new GpuXpbdPose(source, new(skeleton, [new(0), new(1)], new(0, -6, 0),
            damping: 0, step: .1f, iterations: 1));
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0)], [0, 1]);
        Entity target = default;
        CompiledGraph? graph = null;
        var hardware = new PoseBodyHardware();
        var reads = new List<GpuPoseReadback>();
        Vector3 completedPosition = default;
        double completedTime = 0;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                target = GpuPoseAnimation.CreateTarget(frame.World, instance);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(Entity.Null, new(pose));
                CompiledGraph compiled = builder.Compile(); graph = compiled; compiled.Play(frame.World, target);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(instance, binding));
            },
            FrameCompleted = frame =>
            {
                if (frame.Index != 1) return;
                QueryHandle members = frame.World.Query(new QueryDefinitionBuilder().Read<GpuPoseBody>().Read<DeviceBodyBinding>());
                var bodies = new List<Entity>();
                frame.World.ExecuteReadQuery(members, ref bodies, (scoped QueryCursor rows, ref List<Entity> bodies) =>
                {
                    foreach (QueryRow row in rows.Rows) if (row.Read<GpuPoseBody>().Target == target) bodies.Add(row.Entity);
                });
                Assert.Equal(2, bodies.Count);
                Entity moving = bodies.Single(entity => frame.World.Read<GpuPoseBody>(entity).Bone == 1);
                DeviceBodyBinding selected = frame.World.Read<DeviceBodyBinding>(moving);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, members, 2);
                PhysicsSimulation.Create(frame.World, QueryDefinition.Empty, results.Readback(64), 1).Schedule(1, .1, .1).Complete();
                completedPosition = frame.World.Read<BodyPose>(moving).Position;
                completedTime = frame.World.Read<BodyStepState>(moving).Time;
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

        Vector3 expected = new(.99820485f, -.05989229f, 0);
        Assert.InRange(Vector3.Distance(expected, completedPosition), 0, .001f);
        Assert.Equal(.2, completedTime, 8);
        Assert.InRange(Vector3.Distance(expected, Assert.Single(reads).Current.Span[1].Translation), 0, .001f);
    }

    private sealed class PoseBodyHardware : IApplicationGeometryFeature
    {
        internal IGraphicsBackend Backend = null!;
        internal Device Device = null!;
        public IDisposable? Install(ApplicationGeometryContext context)
        { Backend = context.Backend; Device = context.Device; return null; }
    }
}
