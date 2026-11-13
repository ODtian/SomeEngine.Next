using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Physics;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task AHeldSecondarySourceUsesItsCpuParentsCompletedClockForItsBodyAndDisplayedHistory()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(float x, float y = 0) => new()
            { Translation = new(x, y, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var parentDefinition = new Skeleton([-1], [Bone(0)], [1]);
        var definition = new Skeleton(parentDefinition, [-1, 0], [Bone(0), Bone(1)], [2, 3], [0, -1]);
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance child = storage.CreateInstance(definition, parent);
        var clip = QuantizedClipData.Encode(new AnimationClip(definition, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(definition, new GpuBlendClip(clip, Loop: true));
        var rig = new GpuPoseRig(new GpuXpbdPose(blend, new(definition, [new(0), new(1)], Vector3.Zero,
            damping: 0, step: .1f, iterations: 1)));
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0)], [1, 2]);
        var hardware = new PoseBodyHardware();
        var received = new List<GpuPoseReadback>();
        Entity target = default;
        Vector3 anchor = default, position = default;
        double completedTime = 0;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                target = GpuPoseAnimation.CreateTarget(frame.World, child);
                GpuPoseTarget input = frame.World.ReadComponentData<GpuPoseTarget>(target);
                input.Blend = blend; input.Rig = rig; input.Playing = true;
                input.InputNumber = 1; input.CalculationTime = .1; input.PlaybackTime = .1f; input.SampleTime = .1f;
                frame.World.Set(target, input);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(child, binding));
            },
            UpdateFrame = frame => parent.Update([Bone(0, frame.Index == 0 ? 0 : .2f)], (frame.Index + 1) * .1),
            FrameCompleted = frame =>
            {
                if (frame.Index != 1) return;
                Query members = frame.World.Query(new QueryDescription().WithStored<GpuPoseBody>().WithStored<DeviceBodyBinding>());
                var bodies = new List<Entity>();
                using (QueryScope rows = members.OpenRead())
                    foreach (QueryChunk chunk in rows)
                    {
                        ReadOnlySpan<GpuPoseBody> metadata = chunk.Read<GpuPoseBody>();
                        for (int row = 0; row < chunk.Count; row++)
                            if (metadata[row].Target == target) bodies.Add(chunk.Entities[row]);
                    }
                Entity root = bodies.Single(body => frame.World.ReadComponentData<GpuPoseBody>(body).Bone == 0);
                Entity tip = bodies.Single(body => frame.World.ReadComponentData<GpuPoseBody>(body).Bone == 1);
                DeviceBodyBinding selected = frame.World.ReadComponentData<DeviceBodyBinding>(tip);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, members, 2);
                var step = new PhysicsStepContext(frame.World, frame.World.Query(new QueryDescription()), 1, 0, 1, .1, .1);
                results.Readback(64).Schedule(in step, default).Complete();
                anchor = frame.World.ReadComponentData<BodyPose>(root).Position;
                position = frame.World.ReadComponentData<BodyPose>(tip).Position;
                completedTime = frame.World.ReadComponentData<BodyStepState>(tip).Time;
                
            },
        };
        application.InstallGeometry(hardware);

        await SceneImages.RunAsync("d3d12", new SceneScript(application, 2,
            [new(0, edit => edit.ReadGpuPose(target, received.Add)), new(1, edit => edit.ReadGpuPose(target, received.Add))]),
            "--fixed-step", "0.1", "--no-taa");

        // The moving anchor's unit-circle projection of the previous tip (1,0), with zero external acceleration.
        Vector3 expected = new(.9805807f, .003883865f, 0);
        Assert.InRange(Vector3.Distance(new(0, .2f, 0), anchor), 0, .001f);
        Assert.InRange(Vector3.Distance(expected, position), 0, .001f);
        Assert.Equal(.2, completedTime, 8);
        Assert.Equal(2, received.Count);
        GpuPoseReadback result = received[1];
        Assert.InRange(Vector3.Distance(expected, result.Current.Span[1].Translation), 0, .001f);
        Assert.InRange(Vector3.Distance(new(1, 0, 0), result.Previous.Span[1].Translation), 0, .001f);
        Assert.Equal(1ul, result.InputNumber);
        Assert.Equal(.1, result.SourceCalculationTime, 8);
        Assert.Equal(.2, result.CalculationTime, 8);
    }
}
