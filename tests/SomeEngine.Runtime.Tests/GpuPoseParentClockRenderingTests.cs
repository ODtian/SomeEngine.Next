using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
                GpuPoseTarget input = frame.World.Read<GpuPoseTarget>(target);
                input.Blend = blend; input.Rig = rig; input.Playing = true;
                input.InputNumber = 1; input.CalculationTime = .1; input.PlaybackTime = .1f; input.SampleTime = .1f;
                frame.World.Replace(target, input);
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(child, binding));
            },
            UpdateFrame = frame => parent.Update([Bone(0, frame.Index == 0 ? 0 : .2f)], (frame.Index + 1) * .1),
            FrameCompleted = frame =>
            {
                if (frame.Index != 1) return;
                QueryHandle members = frame.World.Query(new QueryDefinitionBuilder().Read<GpuPoseBody>().Read<DeviceBodyBinding>());
                var bodies = new List<Entity>();
                frame.World.ExecuteReadQuery(members, ref bodies, (scoped QueryCursor rows, ref List<Entity> bodies) =>
                { foreach (QueryRow row in rows.Rows) if (row.Read<GpuPoseBody>().Target == target) bodies.Add(row.Entity); });
                Entity root = bodies.Single(body => frame.World.Read<GpuPoseBody>(body).Bone == 0);
                Entity tip = bodies.Single(body => frame.World.Read<GpuPoseBody>(body).Bone == 1);
                DeviceBodyBinding selected = frame.World.Read<DeviceBodyBinding>(tip);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, members, 2);
                var step = new PhysicsStepContext(frame.World, QueryDefinition.Empty, 1, 0, 1, .1, .1);
                results.Readback(64).Schedule(in step, default).Complete();
                anchor = frame.World.Read<BodyPose>(root).Position;
                position = frame.World.Read<BodyPose>(tip).Position;
                completedTime = frame.World.Read<BodyStepState>(tip).Time;
                frame.World.ReleaseQuery(members);
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
