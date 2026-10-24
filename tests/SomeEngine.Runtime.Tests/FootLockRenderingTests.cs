using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task FeetFollowMovingStepsWithReachableLegsAndPreserveTheActionThroughAirSlidingAndToeContact()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0, 5, 6, 7],
            [Bone(new(0, 1.8f, 0)), Bone(new(-.35f, 0, 0)), Bone(new(0, -.9f, 0)), Bone(new(0, -.9f, 0)), Bone(new(0, 0, .3f)),
             Bone(new(.35f, 0, 0)), Bone(new(0, -.9f, 0)), Bone(new(0, -.9f, 0)), Bone(new(0, 0, .3f))],
            [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var action = new AnimationClip(skeleton, 2.5f, ClipSampler.Create<Skeleton, LandingSampler>(skeleton));
        FootMotion motion = FootMotion.Analyze(action, 0, [new(3, 4), new(7, 8)], new(8),
            [new(0, 0, .75f, FootContactKind.Support), new(0, .75f, 1.25f, FootContactKind.Free),
             new(0, 1.25f, 1.75f, FootContactKind.Sliding), new(0, 1.75f, 2.25f, FootContactKind.ToePivot),
             new(0, 2.25f, 2.5f, FootContactKind.Support), new(1, 0, .75f, FootContactKind.Support),
             new(1, .75f, 1.25f, FootContactKind.Free), new(1, 1.25f, 2.5f, FootContactKind.Support)]);
        var path = PosePath.FromClip(action, motion);
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(QuantizedClipData.Encode(action, 8)));
        var left = new GpuTwoBoneChain(skeleton, 1, 2, 3);
        var right = new GpuTwoBoneChain(skeleton, 5, 6, 7);
        var feet = new FootLockRig(0, [new(left, 0, Vector3.UnitZ, Vector3.UnitY), new(right, 1, Vector3.UnitZ, Vector3.UnitY)]);
        var pose = new GpuTwoBonePose(new GpuTwoBonePose(source, left, 0), right, 1);
        var storage = new PoseStorage();
        using SkeletonInstance character = storage.CreateInstance(skeleton), airborneReference = storage.CreateInstance(skeleton);
        Entity target = default, actor = default, leftStep = default, rightStep = default;
        Matrix4x4 actorBase = default;
        CollisionMaintenance<MovingStep, StepReader, SpatialAll, GroupMatch>? maintenance = null;
        var reads = new List<GpuPoseReadback>();
        var actors = new List<Matrix4x4>();
        var metrics = new List<ClusterFrameMetrics>();
        SceneImage? image = null;
        var mesh = new CollisionMesh(new Vector3[] { new(-.25f, 0, -1), new(.25f, 0, -1), new(.25f, 0, 1), new(-.25f, 0, 1) },
            new uint[] { 0, 2, 1, 0, 3, 2 });
        var reader = new StepReader(mesh);
        var application = new ApplicationScene(content.Project.RootPath)
        {
            FrameRendered = metrics.Add,
            Initialize = frame =>
            {
                actor = frame.Scene.MeshEntities[0];
                actorBase = frame.World.Read<WorldTransform>(actor).Qvvs.ToMatrix();
                leftStep = frame.World.CreateEntity(new MovingStep(Matrix4x4.CreateTranslation(-.35f, -.2f, 0) * actorBase, 1));
                rightStep = frame.World.CreateEntity(new MovingStep(Matrix4x4.CreateTranslation(.35f, .4f, 0) * actorBase, 1));
                QueryHandle members = frame.World.Query(frame.World.QueryDefinition().All<MovingStep>());
                CollisionTree tree = Collision.Build<MovingStep, StepReader>(frame.World, members, reader);
                maintenance = Collision.Candidates<MovingStep, StepReader>(frame.World, members, tree, reader).CreateMaintenance(JobSystem.Default);
                maintenance.Schedule().Complete();
                var surfaces = Collision.Query<MovingStep, StepReader>(frame.World, members, tree, reader);
                frame.Systems.AddGameSystem(new StepInputs(surfaces, maintenance, members));
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, new FootLockSystem(), AnimationPhase.Composition);
                target = GpuPoseAnimation.CreateTarget(frame.World, character);
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Parallel())
                {
                    builder.Leaf<GpuTwoBoneParameters, GpuTwoBoneLeaf>(target, new(pose, TranslationBone: feet.PelvisBone, Duration: 2.5f));
                    builder.LockFeet(target, new(feet, new(path, new(0)), surfaces, new(Vector3.UnitY, 2, 2), Duration: 2.5f));
                }
                Entity animation = builder.Play();
                frame.World.ExecuteBufferWrite<FootLockInstance, Entity>(animation, ref actor,
                    static (instances, ref actor) => instances[0].Actor = actor);
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [3, 7]);
                foreach (Entity entity in frame.Scene.MeshEntities) frame.World.Add(entity, new MeshSkin(character, binding));
            },
            UpdateFrame = frame =>
            {
                float step = frame.Index;
                frame.World.Replace(leftStep, new MovingStep(Matrix4x4.CreateTranslation(-.35f + .03f * step, -.2f + .02f * step, 0) * actorBase, (ulong)frame.Index + 2));
                frame.World.Replace(rightStep, new MovingStep(Matrix4x4.CreateTranslation(.35f + .03f * step, .4f + .02f * step, 0) * actorBase, (ulong)frame.Index + 2));
                maintenance!.Schedule().Complete();
                WorldTransform movement = frame.World.Read<WorldTransform>(actor);
                movement.Qvvs.Position = actorBase.Translation + new Vector3(.02f * step, 0, 0);
                frame.World.Replace(actor, movement);
                actors.Add(movement.Qvvs.ToMatrix());
                frame.Scene.ReadGpuPose(target, reads.Add);
                if (frame.Index == 9) frame.Scene.CaptureImage(value => image = value);
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 10, []), "--no-taa", "--no-vsm", "--fixed-step", ".25");

        Assert.Equal(10, reads.Count);
        for (int frame = 0; frame < 2; frame++)
        {
            FootNear(Vector3.Transform(new(-.35f + .03f * frame, -.2f + .02f * frame, 0), actorBase),
                Vector3.Transform(reads[frame].Current.Span[3].Translation, actors[frame]));
            FootNear(Vector3.Transform(new(.35f + .03f * frame, .4f + .02f * frame, 0), actorBase),
                Vector3.Transform(reads[frame].Current.Span[7].Translation, actors[frame]));
            Assert.InRange(reads[frame].Current.Span[0].M42, 1.59f, 1.63f);
            foreach (int root in new[] { 1, 5 })
            {
                Matrix4x4[] bones = reads[frame].Current.ToArray();
                Assert.InRange(MathF.Abs(Vector3.Distance(bones[root].Translation, bones[root + 1].Translation) - .9f), 0, .001f);
                Assert.InRange(MathF.Abs(Vector3.Distance(bones[root + 1].Translation, bones[root + 2].Translation) - .9f), 0, .001f);
            }
        }
        for (int frame = 2; frame < 4; frame++)
        {
            var locals = new BoneTransform[skeleton.BoneCount];
            action.SampleLocal((frame + 1) * .25f, locals);
            airborneReference.Update(locals, (frame + 1) * .25);
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
                GpuPoseExamples.Near(airborneReference.ModelPose.Transforms[bone], reads[frame].Current.Span[bone]);
        }
        for (int frame = 4; frame < 6; frame++)
        {
            FootNear(Vector3.Transform(new(-.35f + .08f * ((frame + 1) * .25f) + .02f * frame, -.2f + .02f * frame, 0), actorBase),
                Vector3.Transform(reads[frame].Current.Span[3].Translation, actors[frame]));
        }
        Vector3 firstToe = Vector3.Transform(reads[6].Current.Span[4].Translation, actors[6]);
        Vector3 nextToe = Vector3.Transform(reads[7].Current.Span[4].Translation, actors[7]);
        FootNear(new(.03f, .02f, 0), nextToe - firstToe);
        Assert.InRange(nextToe.Y - actorBase.M42, -.061f, -.059f);
        Assert.True(reads[7].Current.Span[3].M42 > reads[7].Current.Span[4].M42 + .11f);
        FootNear(Vector3.Transform(new(0, -.02f, 0), actorBase), Vector3.Transform(reads[9].Current.Span[3].Translation, actors[9]));
        FootNear(Vector3.Transform(new(.68f, .58f, 0), actorBase), Vector3.Transform(reads[9].Current.Span[7].Translation, actors[9]));
        Assert.True(DrawnClusters(metrics[^1]) > 0);
        int visible = 0;
        foreach (byte value in Assert.IsType<SceneImage>(image).Pixels.Span)
            if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
    }

    private static void FootNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < .001f, $"Expected {expected}; actual {actual}.");

    private readonly record struct MovingStep(Matrix4x4 Transform, ulong Version) : IComponent;
    private readonly struct StepReader(CollisionMesh mesh) : ICollisionSurfaceReader<MovingStep>
    {
        public CollisionBounds Read(in MovingStep step) => ReadSurface(in step).Bounds;
        public CollisionSurface ReadSurface(in MovingStep step) => new(mesh, step.Transform, step.Version);
    }
    private readonly struct StepInputs(CollisionQueries<MovingStep, StepReader> surfaces,
        CollisionMaintenance<MovingStep, StepReader, SpatialAll, GroupMatch> maintenance, QueryHandle members) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) { }
        public void OnDestroy(ref ImmediateSystemContext context)
        {
            surfaces.Dispose(); maintenance.Dispose(); context.World.ReleaseQuery(members);
        }
    }
    private readonly struct LandingSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation.X = .08f * time;
            if (time >= .75f && time < 1.25f)
            {
                destination[0].Translation.Y += .6f + .2f * MathF.Sin((time - .75f) * 2 * MathF.PI);
                destination[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f + .3f * MathF.Sin((time - .75f) * 2 * MathF.PI));
                destination[5].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.2f);
            }
            if (time >= 1.75f && time <= 2)
                destination[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.6f * (time - 1.75f));
            else if (time > 2 && time < 2.25f)
                destination[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.6f * (2.25f - time));
        }
    }
}
