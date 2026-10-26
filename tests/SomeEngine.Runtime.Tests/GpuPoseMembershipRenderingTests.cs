using System.Numerics;
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
    private readonly struct ContinuingPoseBody : IComponent;

    [Fact]
    public async Task AddingAndRebindingDeviceActorsPreservesTheContinuingBodyAndItsDisplayedHistory()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(float x) => new() { Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(0), Bone(1)], [1, 2]);
        var longer = new Skeleton([-1, 0, 1], [Bone(0), Bone(1), Bone(.5f)], [1, 2, 3]);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), continuing = storage.CreateInstance(skeleton),
            added = storage.CreateInstance(skeleton), changed = storage.CreateInstance(longer);
        GpuPoseBlend Source(Skeleton definition) => new(definition, new GpuBlendClip(QuantizedClipData.Encode(
            new AnimationClip(definition, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4), Loop: true));
        GpuPoseBlend source = Source(skeleton), changedSource = Source(longer);
        var rig = new GpuPoseRig(new GpuXpbdPose(source, new(skeleton, [new(0), new(1)], new(0, -6, 0), damping: 0, step: .1f, iterations: 1)));
        var changedRig = new GpuPoseRig(new GpuXpbdPose(changedSource, new(longer, [new(0), new(1), new(2)], new(0, -6, 0),
            damping: 0, step: .1f, iterations: 1)));
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0)], [0, 1]);
        var hardware = new PoseBodyHardware();
        var reads = new List<GpuPoseReadback>();
        var bodyPositions = new List<Vector3>();
        Entity firstTarget = default, continuingTarget = default, addedTarget = default, tip = default;
        Entity[] initialBodies = [];
        QueryHandle observed = default;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                firstTarget = GpuPoseAnimation.CreateTarget(frame.World, first);
                continuingTarget = GpuPoseAnimation.CreateTarget(frame.World, continuing);
                observed = frame.World.Query(new QueryDefinitionBuilder().All<ContinuingPoseBody>().Read<DeviceBodyBinding>());
                foreach (Entity mesh in frame.Scene.MeshEntities) frame.World.Add(mesh, new MeshSkin(continuing, binding));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) addedTarget = GpuPoseAnimation.CreateTarget(frame.World, added);
                bool rebound = frame.Index == 3;
                Write(frame, firstTarget, rebound ? changed : first, rebound ? changedSource : source, rebound ? changedRig : rig);
                Write(frame, continuingTarget, continuing, source, rig);
                if (frame.Index >= 2) Write(frame, addedTarget, added, source, rig);
            },
            FrameCompleted = frame =>
            {
                QueryHandle members = frame.World.Query(new QueryDefinitionBuilder().Read<GpuPoseBody>().Read<DeviceBodyBinding>());
                var bodies = new List<Entity>();
                frame.World.ExecuteReadQuery(members, ref bodies, (scoped QueryCursor rows, ref List<Entity> bodies) =>
                {
                    foreach (QueryRow row in rows.Rows)
                        if (row.Read<GpuPoseBody>().Target == continuingTarget) bodies.Add(row.Entity);
                });
                if (frame.Index == 0)
                {
                    initialBodies = bodies.ToArray();
                    foreach (Entity body in bodies) frame.World.Add(body, new ContinuingPoseBody());
                    tip = bodies.Single(body => frame.World.Read<GpuPoseBody>(body).Bone == 1);
                }
                Assert.Equal(initialBodies.OrderBy(body => body.Index), bodies.OrderBy(body => body.Index));
                DeviceBodyBinding selected = frame.World.Read<DeviceBodyBinding>(tip);
                using var results = new DeviceBodyResults(hardware.Backend, hardware.Device, selected.Bodies, frame.World, observed, 2);
                var completed = new PhysicsStepContext(frame.World, QueryDefinition.Empty, frame.Index + 1, 0, 1, frame.Index * .1, .1);
                results.Readback(64).Schedule(in completed, default).Complete();
                bodyPositions.Add(frame.World.Read<BodyPose>(tip).Position);
                Assert.Equal((frame.Index + 1) * .1, frame.World.Read<BodyStepState>(tip).Time, 8);
                frame.World.ReleaseQuery(members);
            },
            Shutdown = frame => frame.World.ReleaseQuery(observed),
        };
        application.InstallGeometry(hardware);

        await SceneImages.RunAsync("d3d12", new SceneScript(application, 6,
            Enumerable.Range(0, 6).Select(index => new SceneAction(index, edit => edit.ReadGpuPose(continuingTarget, reads.Add)))),
            "--fixed-step", "0.1", "--no-taa");

        // Independent unit-circle projection after each .1 s gravity prediction, with the first shown pose as its start.
        Vector3[] expected = [new(1, 0, 0), new(.99820485f, -.05989229f, 0), new(.98410905f, -.17756515f, 0),
            new(.9390119f, -.3438847f, 0), new(.8430846f, -.5377810f, 0), new(.6863622f, -.7272599f, 0)];
        Assert.Equal(6, reads.Count); Assert.Equal(6, bodyPositions.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.InRange(Vector3.Distance(expected[index], bodyPositions[index]), 0, .001f);
            Assert.InRange(Vector3.Distance(expected[index], reads[index].Current.Span[1].Translation), 0, .001f);
            Assert.InRange(Vector3.Distance(expected[Math.Max(0, index - 1)], reads[index].Previous.Span[1].Translation), 0, .001f);
            Assert.Equal((index + 1) * .1, reads[index].CalculationTime, 8);
        }

        static void Write(ApplicationFrame frame, Entity target, SkeletonInstance instance, GpuPoseBlend blend, GpuPoseRig rig)
        {
            GpuPoseTarget input = frame.World.Read<GpuPoseTarget>(target);
            input.Skeleton = instance; input.Blend = blend; input.Rig = rig; input.Playing = true;
            input.InputNumber = (ulong)frame.Index + 1; input.CalculationTime = (frame.Index + 1) * .1;
            input.PlaybackTime = (frame.Index + 1) * .1f; input.SampleTime = input.PlaybackTime;
            frame.World.Replace(target, input);
        }
    }
}
