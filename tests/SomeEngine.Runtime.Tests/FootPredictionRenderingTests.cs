using System.Numerics;
using System.Text.Json;
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
    public async Task SelectedSequencePredictsClearSwingAndTimedLandingsAcrossFlatSlopeStepsAndContinuesAfterASwitch()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0, 5, 6, 7],
            [Bone(new(0, 1.6f, 0)), Bone(new(-.3f, 0, 0)), Bone(new(0, -1, 0)), Bone(new(0, -1, 0)), Bone(new(0, 0, .2f)),
             Bone(new(.3f, 0, 0)), Bone(new(0, -1, 0)), Bone(new(0, -1, 0)), Bone(new(0, 0, .2f))],
            [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        // Step and redirected-step are example action fixtures for the general selected-sequence model.
        var step = new StepAction(skeleton, .8f, 0);
        var redirected = new StepAction(skeleton, 1, .25f);
        AnimationClip stepClip = new(skeleton, 2, ClipSampler.Create<StepAction, StepActionSampler>(step), rootMotionBone: 0);
        AnimationClip redirectedClip = new(skeleton, 2, ClipSampler.Create<StepAction, StepActionSampler>(redirected), rootMotionBone: 0);
        PosePath selected = SelectedStep(step), changed = SelectedStep(redirected);
        var left = new GpuTwoBoneChain(skeleton, 1, 2, 3);
        var right = new GpuTwoBoneChain(skeleton, 5, 6, 7);
        var feet = new FootLockRig(0, [new(left, 0, Vector3.UnitZ, Vector3.UnitY), new(right, 1, Vector3.UnitZ, Vector3.UnitY)]);
        GpuTwoBonePose GpuAction(AnimationClip clip) => new(new GpuTwoBonePose(
            new GpuPoseBlend(skeleton, new GpuBlendClip(QuantizedClipData.Encode(clip, 32))), left, 0), right, 1);
        GpuTwoBonePose source = GpuAction(stepClip);
        var changedRig = new GpuPoseRig(GpuAction(redirectedClip), feet.PelvisBone);
        var warp = new RootMotionWarp(RootMotionPath.FromClip(stepClip, 32), new(.25f, 1.5f))
            .Bind(Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, .2f));
        var storage = new PoseStorage();
        using SkeletonInstance flat = storage.CreateInstance(skeleton), slope = storage.CreateInstance(skeleton),
            steps = storage.CreateInstance(skeleton), switched = storage.CreateInstance(skeleton);
        SkeletonInstance[] characters = [flat, slope, steps, switched];
        Entity[] targets = new Entity[4], actors = new Entity[4], animations = new Entity[4];
        var trajectories = new FootMovementTrajectory[4];
        var reads = Enumerable.Range(0, 4).Select(_ => new List<GpuPoseReadback>()).ToArray();
        var actorMatrices = Enumerable.Range(0, 4).Select(_ => new List<Matrix4x4>()).ToArray();
        var observed = Enumerable.Range(0, 4).Select(_ => new List<FootFrame>()).ToArray();
        var metrics = new List<ClusterFrameMetrics>();
        var times = new List<ApplicationFrameTiming>();
        SceneImage? image = null;
        var positions = new List<Vector3>();
        var indices = new List<uint>();
        GroundQuad(-3.8f, -2.2f, 5, 8, 0, 0);
        GroundQuad(-1.8f, -.2f, 5, 8, -.25f, .5f);
        GroundQuad(.2f, 1.8f, 5, 6.4f, 0, 0); GroundQuad(.2f, 1.8f, 6.4f, 8, .35f, .35f);
        GroundQuad(2.2f, 3.8f, 5, 6.4f, 0, 0); GroundQuad(2.2f, 3.8f, 6.4f, 8, .35f, .35f);
        var reader = new StepReader(new CollisionMesh(positions.ToArray(), indices.ToArray()));
        var application = new ApplicationScene(content.Project.RootPath)
        {
            FrameRendered = metrics.Add,
            FrameMeasured = times.Add,
            Initialize = frame =>
            {
                frame.World.CreateEntity(new MovingStep(Matrix4x4.Identity, 1));
                QueryHandle members = frame.World.Query(frame.World.QueryDefinition().All<MovingStep>());
                CollisionTree tree = Collision.Build<MovingStep, StepReader>(frame.World, members, reader);
                var maintenance = Collision.Candidates<MovingStep, StepReader>(frame.World, members, tree, reader).CreateMaintenance(JobSystem.Default);
                maintenance.Schedule().Complete();
                var surfaces = Collision.Query<MovingStep, StepReader>(frame.World, members, tree, reader);
                frame.Systems.AddGameSystem(new StepInputs(surfaces, maintenance, members));
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                AnimationSystems.Add(frame.Systems.Animation, new FootLockSystem(), AnimationPhase.Composition);
                AnimationSystems.Add(frame.Systems.Animation, new ObserveFootFrames(animations, targets, observed), AnimationPhase.Composition);
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [3, 7]);
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Parallel())
                {
                    builder.Leaf<GpuTwoBoneParameters, GpuTwoBoneLeaf>(Entity.Null, new(source, feet.PelvisBone, 2));
                    builder.LockFeet(Entity.Null, new(feet, new(selected, new(0), 1), surfaces, new(Vector3.UnitY, 2, 2),
                        ContactWindow: 2, Duration: 2, Prediction: new(TransitionDuration: .35f)));
                }
                using CompiledGraph graph = builder.Compile();
                Entity[] meshes = frame.Scene.MeshEntities.ToArray();
                for (int subject = 0; subject < 4; subject++)
                {
                    actors[subject] = meshes[subject];
                    var position = new Vector3(-3 + 2 * subject, 0, 6);
                    frame.World.Replace(actors[subject], new WorldTransform { Qvvs = new(position, Quaternion.Identity) });
                    trajectories[subject] = new([new(0, position, new(.04f, 0, 0), 0), new(2, position + new Vector3(.08f, 0, 0), new(.04f, 0, 0), 0)]);
                    targets[subject] = GpuPoseAnimation.CreateTarget(frame.World, characters[subject]);
                    animations[subject] = graph.Play(frame.World, targets[subject]);
                    var input = (Actor: actors[subject], Movement: new FootMovementInput(trajectories[subject], warp));
                    frame.World.ExecuteBufferWrite<FootLockInstance, (Entity Actor, FootMovementInput Movement)>(animations[subject], ref input,
                        static (instances, ref input) => { instances[0].Actor = input.Actor; instances[0].Movement = input.Movement; });
                    frame.World.Add(actors[subject], new MeshSkin(characters[subject], binding));
                }
            },
            UpdateFrame = frame =>
            {
                float time = (frame.Index + 1) / 16f;
                for (int subject = 0; subject < 4; subject++)
                {
                    var position = new Vector3(-3 + 2 * subject + .04f * time, 0, 6);
                    Matrix4x4 actor = warp.Sample(time) * Matrix4x4.CreateTranslation(position);
                    frame.World.Replace(actors[subject], new WorldTransform { Qvvs = new(actor.Translation, Quaternion.Identity) });
                    trajectories[subject].Points[0] = new(time, actor.Translation, new(.04f, 0, 0), 0);
                    trajectories[subject].Points[1] = new(time + 2, actor.Translation + new Vector3(.08f, 0, 0), new(.04f, 0, 0), 0);
                    actorMatrices[subject].Add(actor);
                    int selectedSubject = subject;
                    frame.Scene.ReadGpuPose(targets[subject], read => reads[selectedSubject].Add(read));
                }
                if (frame.Index == 11)
                {
                    var request = new PosePredictionRequest(changed, new(0), 2);
                    frame.World.ExecuteBufferWrite<FootLockInstance, PosePredictionRequest>(animations[3], ref request,
                        static (instances, ref request) => instances[0].Source = request);
                    frame.World.ExecuteBufferWrite<GpuRigInstance, GpuPoseRig>(animations[3], ref changedRig,
                        static (instances, ref rig) => instances[0].Rig = rig);
                }
                if (frame.Index == 31) frame.Scene.CaptureImage(value => image = value);
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 32, []), "--no-taa", "--no-vsm", "--fixed-step", ".0625");

        Vector3 Foot(int subject, int frame, int bone = 3) => Vector3.Transform(reads[subject][frame].Current.Span[bone].Translation, actorMatrices[subject][frame]);
        Assert.True(Foot(2, 8).Y > .18f, $"Early step clearance: {Foot(2, 8)}");
        Assert.True(Foot(0, 12).Y > .06f);
        Assert.True(Foot(1, 12).Y > .25f * (Foot(1, 12).Z - 6) + .04f);
        Assert.True(Foot(2, 12).Y > .39f);
        Assert.True(reads[2][8].Current.Span[0].M42 > 1.61f);
        for (int subject = 0; subject < 4; subject++)
        {
            Assert.Equal(32, reads[subject].Count);
            float height = subject == 0 ? 0 : subject == 1 ? .25f : .35f;
            float distance = subject == 3 ? 1.2f : 1;
            FootNear(new(-3 + 2 * subject - .24f, height, 6 + distance), Foot(subject, 23));
            FootNear(Foot(subject, 23), Foot(subject, 31));
            Assert.Equal(1.5f, reads[subject][23].SampleTime);
            Assert.Equal(2, reads[subject][31].SampleTime);
            FootFrame prior = observed[subject].Single(value => value.Time == 1.4375);
            Assert.Equal(FootContactKind.Free, prior.Contacts[0].Kind);
            Assert.Equal(1.5, prior.Landings[0].Contact.Time);
            Assert.Equal(subject == 3 ? 2ul : 1ul, prior.Landings[0].Pose.Request.Conditions);
            FootNear(new(-3 + 2 * subject + .06f, 1.6f, 6.2f), prior.Landings[0].RootWorld.Translation);
            FootFrame landed = observed[subject].Single(value => value.Time == 1.5);
            Assert.Equal(FootContactKind.Support, landed.Contacts[0].Kind);
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,
                reads[subject][23].Current.Span[3] * actorMatrices[subject][23]));
            Assert.True(Vector3.Dot(normal, subject == 1 ? Vector3.Normalize(new(0, 1, -.25f)) : Vector3.UnitY) > .999f);
        }
        for (int frame = 10; frame <= 16; frame++)
        {
            Assert.True(Vector3.Distance(Foot(3, frame - 1), Foot(3, frame)) < .16f);
            Vector3 acceleration = Foot(3, frame) - 2 * Foot(3, frame - 1) + Foot(3, frame - 2);
            Assert.True(acceleration.Length() < .07f, $"Switch handoff at {frame}: {acceleration}");
            Assert.InRange(MathF.Abs(reads[3][frame].Current.Span[0].M42 - reads[3][frame - 1].Current.Span[0].M42), 0, .06f);
        }
        Assert.True(DrawnClusters(metrics[^1]) > 0);
        Assert.True(Assert.IsType<SceneImage>(image).Pixels.ToArray().Count(value => value > 32 && value < 255) > 1000);
        FootFrame[] predictionFrames = observed.SelectMany(value => value).Where(value => value.Time > .25 && value.Time < 1.5).ToArray();
        Assert.True(predictionFrames.Sum(value => value.Costs.PoseSampling.TotalMilliseconds) > 0);
        Assert.True(predictionFrames.Sum(value => value.Costs.GeometryQueries.TotalMilliseconds) > 0);
        _output.WriteLine("Example fixtures: step and redirected-step, 4 independent 9-bone characters/4 panels; same selected sequence on flat/slope/steps, one mid-action selection change; 32 frames at 16 Hz, D3D12, GPU final-bone readback and normal Cluster presentation in the measured flow.");
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            PoseSamplingMs = Distribution(predictionFrames.Select(value => value.Costs.PoseSampling.TotalMilliseconds)),
            GeometryQueriesMs = Distribution(predictionFrames.Select(value => value.Costs.GeometryQueries.TotalMilliseconds)),
            CpuFrameMs = Distribution(times.Select(value => value.CpuMs)),
            AnimationMs = Distribution(times.Select(value => value.AnimationMs)),
            GpuFrameMs = Distribution(times.Select(value => value.Gpu.ElapsedMs)),
        }));

        PosePath SelectedStep(StepAction action)
        {
            float[] starts = [0, .25f, 1.5f], lengths = [.25f, 1.25f, .5f];
            var parts = new PosePath[3];
            for (int part = 0; part < parts.Length; part++)
            {
                var clip = new AnimationClip(skeleton, lengths[part], ClipSampler.Create<StepAction, StepActionSampler>(action with { Offset = starts[part] }));
                FootMotion motion = FootMotion.Analyze(clip, 0, [new(3, 4), new(7, 8)], new(32),
                    [new(0, 0, lengths[part], part == 1 ? FootContactKind.Free : FootContactKind.Support), new(1, 0, lengths[part], FootContactKind.Support)]);
                parts[part] = PosePath.FromClip(clip, motion);
            }
            return PosePath.Sequence(parts);
        }
        void GroundQuad(float leftX, float rightX, float startZ, float endZ, float startY, float endY)
        {
            uint first = (uint)positions.Count;
            positions.AddRange([new(leftX, startY, startZ), new(rightX, startY, startZ), new(rightX, endY, endZ), new(leftX, endY, endZ)]);
            indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
    }

    private readonly record struct FootFrame(double Time, FootContactTarget[] Contacts, FootLandingPrediction[] Landings, FootPlacementCosts Costs);
    private readonly struct ObserveFootFrames(Entity[] animations, Entity[] targets, List<FootFrame>[] frames) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            for (int subject = 0; subject < animations.Length; subject++)
            {
                var data = (Time: context.World.Read<GpuPoseTarget>(targets[subject]).CalculationTime, Frames: frames[subject]);
                context.World.ExecuteBufferRead<FootLockInstance, (double Time, List<FootFrame> Frames)>(animations[subject], ref data,
                    static (instances, ref data) => data.Frames.Add(new(data.Time, instances[0].Contacts.ToArray(), instances[0].Landings.ToArray(), instances[0].Costs)));
            }
        }
    }
    private sealed record StepAction(Skeleton Skeleton, float Distance, float Yaw, float Offset = 0);
    private readonly struct StepActionSampler : IClipSampler<StepAction>
    {
        public static void SampleLocal(StepAction data, float time, Span<BoneTransform> destination)
        {
            data.Skeleton.ReferencePose.CopyTo(destination);
            float amount = Math.Clamp((time + data.Offset - .25f) / 1.25f, 0, 1);
            float progress = amount * amount * (3 - 2 * amount);
            float height = .04f * MathF.Sin(MathF.PI * amount), distance = data.Distance * progress;
            SetLeg(destination, 1, distance, height, data.Yaw * progress);
            SetLeg(destination, 5, 0, 0, 0);
        }
        private static void SetLeg(Span<BoneTransform> destination, int root, float forward, float lift, float yaw)
        {
            float vertical = 1.6f - lift;
            float halfBend = MathF.Acos(MathF.Sqrt(vertical * vertical + forward * forward) / 2);
            float center = -MathF.Atan2(forward, vertical);
            destination[root].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, center - halfBend);
            destination[root + 1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 2 * halfBend);
            destination[root + 2].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -center - halfBend)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
    }
}
