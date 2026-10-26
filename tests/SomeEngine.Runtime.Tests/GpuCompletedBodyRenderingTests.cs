using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task CompletedDeviceBodiesUseTheSelectedGameSourceAndAdvanceDisplayedHistoryAtAHeldInput()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(float x) => new() { Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(0), Bone(1)], [1, 2]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        var rig = new GpuPoseRig(new GpuXpbdPose(blend, new(skeleton, [new(0), new(1)], Vector3.Zero)));
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0)], [0, 1]);
        var hardware = new PoseBodyHardware();
        var received = new List<GpuPoseReadback>();
        Entity target = default;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                target = GpuPoseAnimation.CreateTarget(frame.World, instance);
                GpuPoseTarget input = frame.World.Read<GpuPoseTarget>(target);
                input.Blend = blend; input.Rig = rig; input.Playing = true;
                input.InputNumber = 1; input.CalculationTime = .1; input.PlaybackTime = .1f; input.SampleTime = .1f;
                frame.World.Replace(target, input);
                var completed = new CompletedPoseBodies(hardware.Backend, hardware.Device, frame.World, target);
                frame.Systems.Animation.Add(completed);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(instance, binding));
            },
        };
        application.InstallGeometry(hardware);

        await SceneImages.RunAsync("d3d12", new SceneScript(application, 3,
            [new(0, edit => edit.ReadGpuPose(target, received.Add)), new(1, edit => edit.ReadGpuPose(target, received.Add)),
             new(2, edit => edit.ReadGpuPose(target, received.Add))]), "--fixed-step", "0.1", "--no-taa");

        Assert.Equal(3, received.Count);
        Vector3[] positions = [new(.9f, .2f, 0), new(.8f, .3f, 0), new(.7f, .4f, 0)];
        for (int index = 0; index < received.Count; index++)
        {
            GpuPoseReadback result = received[index];
            Assert.InRange(Vector3.Distance(positions[index], result.Current.Span[1].Translation), 0, .001f);
            Assert.InRange(Vector3.Distance(positions[Math.Max(0, index - 1)], result.Previous.Span[1].Translation), 0, .001f);
            Assert.Equal(1ul, result.InputNumber);
            Assert.Equal(.1, result.SourceCalculationTime, 8);
            Assert.Equal((index + 1) * .1, result.CalculationTime, 8);
            if (index > 0) Assert.True(result.Version > received[index - 1].Version);
        }
    }

    /// <summary>A project writes its completed body representation, then uses the shared GPU bone mapping and extraction.</summary>
    private sealed class CompletedPoseBodies : ISystem<ImmediateSystemContext>
    {
        private readonly World _world;
        private readonly QueryHandle _targets, _members;
        private readonly DevicePosePhysics _physics;
        private readonly DevicePoseSources _sources;
        private readonly DevicePoseBodyStages _stages;
        private readonly DeviceBodyInputs _inputs;
        private readonly Entity _tip;
        private JobHandle _completion;
        private long _number;

        internal CompletedPoseBodies(IGraphicsBackend backend, Device device, World world, Entity target)
        {
            _world = world;
            _targets = world.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());
            _physics = new(backend, device, world, new(2, 1, 1)); _physics.Bind(target);
            _tip = _physics.GetBodies(target, 0).Span[1];
            _sources = new(backend, device, world, _targets, DevicePoseShaders.Get(device.Capabilities.CompileTarget),
                new(1, 2, 1, 2, 1, 0, 1, 1), _physics);
            _stages = _sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
            _members = world.Query(new QueryDefinitionBuilder().Read<GpuPoseBody>().Read<DeviceBodyBinding>());
            _inputs = new(world, _members, _physics.Bodies);
            var first = new PhysicsStepContext(world, QueryDefinition.Empty, 0, 0, 1, 0, 0);
            _physics.Upload().Then(_sources.Evaluate()).Schedule(in first, default).Complete();
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (context.DeltaTime == 0) return;
            _number++;
            Vector3 position = _number == 1 ? new(.9f, .2f, 0) : _number == 2 ? new(.8f, .3f, 0) : new(.7f, .4f, 0);
            _world.Replace(_tip, new BodyPose { Position = position, Rotation = Quaternion.Identity });
            var step = new PhysicsStepContext(_world, QueryDefinition.Empty, _number, 0, 1, (_number - 1) * .1, .1);
            _completion = _stages.WriteTargets().Then(_inputs.Upload()).Then(_stages.MapBones())
                .Then(_sources.CopyOutput()).Schedule(in step, _completion);
        }

        public void OnDestroy(ref ImmediateSystemContext context)
        {
            _completion.Complete(); _inputs.Dispose(); _sources.Dispose(); _physics.Dispose();
            _world.ReleaseQuery(_members); _world.ReleaseQuery(_targets);
        }
    }
}
