using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PhysicsPoseBehaviorTests
{
    private static readonly Channel<float> PhysicsWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);

    [Fact]
    public void CompletedBodyPosesFollowTheirBoneChainAndPartWeightsWhileRecoveryReturnsToTheCurrentAnimation()
    {
        var skeleton = new Skeleton([-1, 0, -1, 2],
        [
            Transform(Vector3.Zero, 0), Transform(Vector3.UnitY, 0),
            Transform(Vector3.Zero, MathF.PI / 2), Transform(-Vector3.UnitY, 0),
        ], [1, 2, 3, 4]);
        var mapping = new PhysicsPoseMap(skeleton,
        [
            new(1, Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 1, 0)),
            new(3, Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-1, 0, 0)),
            new(0, Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 0, 0)),
        ]);
        var clip = new AnimationClip(skeleton, 4, ClipSampler.Create<Skeleton, MovingSampler>(skeleton));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 3, output: new(2));
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 1 };
        world.Add(target, layers);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(target, new(new PoseBlend([new(new(1))]), new(2), new(StageEnd.Settled)));
            builder.BlendPhysicsPose(target, new(new(0), new(1), mapping, new BoneMask([1, .5f, 0, 1])), layer: 1);
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0), Loop: true));
        }
        Entity animation = builder.Play();
        Entity[] bodies =
        [
            CreateBody(world, Matrix4x4.CreateRotationX(MathF.PI / 2) *
                Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(8, 22, 0)),
            CreateBody(world, Matrix4x4.CreateRotationY(MathF.PI / 2) *
                Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(9, 26, 0)),
            CreateBody(world, Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(9, 22, 0)),
        ];
        QueryHandle bodyPoses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        var binding = (Bodies: bodies, Query: bodyPoses, Read: default(ReadPhysicsPose));
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity[] Bodies, QueryHandle Query, ReadPhysicsPose Read)>(animation, ref binding,
            static (instances, ref binding) =>
        {
            ref PhysicsPoseInstance input = ref instances[0];
            input.ActorTransform = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 0);
            binding.Read = input.BindBodies(binding.Query, binding.Bodies);
        });
        using var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, new PredictBodies(Vector3.Zero, 64), 1)
            .CreateFrame(binding.Read, stepDuration: .1, maximumDelay: 0, firstStepNumber: 1, startTime: 0);
        physics.AdvanceTime(.1);
        physics.CompleteForRead();
        var recovery = Spec.Tween<float, FloatOps, Linear>(1);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, recovery);
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new PoseBlendSystem());
            nodes.Add(new PhysicsPoseSystem());
        });

        Tick(systems, world, 0);
        Near(new(2, 0, 0), instance.LocalPose[0].Translation);
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new(1.5f, .5f, 0), instance.LocalPose[1].Translation);
        Near(new(.70710677f, .70710677f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[1].Rotation));
        Near(new(1.5f, 1.5f, 0), instance.ModelPose.Transforms[1].Translation);
        Near(new(-.70710677f, .70710677f, 0), Vector3.TransformNormal(Vector3.UnitX, instance.ModelPose.Transforms[1]));
        Near(new(2, -6, 0), instance.LocalPose[3].Translation);
        Near(new(6, 2, 0), instance.ModelPose.Transforms[3].Translation);

        PropertyAnimation.To(world, target, PhysicsWeight, 0f, recovery);
        Tick(systems, world, .0001);
        Assert.InRange(Vector3.Distance(new(2, 0, 0), instance.LocalPose[0].Translation), 0, .001f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation)), 0, .001f);
        Tick(systems, world, .4999);
        Near(new(1.25f, 0, 0), instance.LocalPose[0].Translation);
        Near(new(.70710677f, .70710677f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new(.75f, .75f, 0), instance.LocalPose[1].Translation);
        Near(new(.75f, -3.5f, 0), instance.LocalPose[3].Translation);
        Near(new(3.5f, 1.25f, 0), instance.ModelPose.Transforms[3].Translation);

        Tick(systems, world, .5);
        Near(new(1, 0, 0), instance.LocalPose[0].Translation);
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(Vector3.UnitY, instance.LocalPose[1].Translation);
        Near(-Vector3.UnitY, instance.LocalPose[3].Translation);
        Near(new(1, 1, 0), instance.ModelPose.Transforms[1].Translation);
        Near(new(1, 1, 0), instance.ModelPose.Transforms[3].Translation);
        Assert.Equal(1, instance.ModelPose.SampleTime, 5);
        Tick(systems, world, .5);
        Near(new(1.5f, 0, 0), instance.LocalPose[0].Translation);
        Near(new(1, 1.5f, 0), instance.ModelPose.Transforms[3].Translation);
        world.ExecuteBufferRead<PhysicsPoseInstance>(animation, static inputs =>
        {
            Assert.Equal(1, inputs[0].Step!.Value.Number);
            Assert.Equal(.1, inputs[0].Step!.Value.EndTime);
            Assert.True(inputs[0].Step!.Value.Completion.IsCompleted);
        });
        world.ReleaseQuery(bodyPoses);
    }

    [Fact]
    public async Task MappedBoneResultsRemainReadableUntilTheirConsumptionLetsTheNextPhysicsStepWrite()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var skeleton = new Skeleton([-1], [Transform(Vector3.Zero, 0)], [1]);
        var clip = new AnimationClip(skeleton, 4, ClipSampler.Create<Skeleton, MovingRootSampler>(skeleton));
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new(1));
        Entity animation;
        {
            using var builder = new AnimationBuilder(world);
            using (builder.Parallel())
            {
                builder.BlendPhysicsPose(target, new(new(0), new(1), new PhysicsPoseMap(skeleton, [new(0, Matrix4x4.Identity)]),
                    new BoneMask([1])));
                builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0), Loop: true));
            }
            animation = builder.Play();
        }
        Entity body = CreateBody(world, Matrix4x4.Identity);
        world.Replace(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitX });
        QueryHandle bodyPoses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        var binding = (Body: body, Query: bodyPoses, Read: default(ReadPhysicsPose));
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity Body, QueryHandle Query, ReadPhysicsPose Read)>(animation, ref binding,
            static (inputs, ref binding) => binding.Read = inputs[0].BindBodies(binding.Query, [binding.Body]));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        using var mapped = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        using var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new RecordPredictedStep(events), 1).CreateFrame(new WaitForMappedPose(binding.Read, mapped, release, events, scheduler),
            stepDuration: .1, maximumDelay: 0, firstStepNumber: 1, startTime: 0);

        Tick(systems, world, .05);
        Near(new(.05f, 0, 0), instance.ModelPose.Transforms[0].Translation);
        physics.AdvanceTime(.1);
        _ = physics.ConsumeCompleted();
        try
        {
            Assert.True(mapped.Wait(TimeSpan.FromSeconds(10)));
            physics.AdvanceTime(.1);
            physics.SubmitDueSteps();
            await Task.Run(() => Tick(systems, world, .1)).WaitAsync(TimeSpan.FromSeconds(10));
            Near(new(.1f, 0, 0), instance.ModelPose.Transforms[0].Translation);
            events.Enqueue("readers-1");
        }
        finally { release.Set(); }

        PhysicsStepResult completed = physics.CompleteForRead();
        Tick(systems, world, .1);

        Assert.Equal(2, completed.Number);
        Assert.Equal(.2, completed.EndTime);
        Near(new(.2f, 0, 0), instance.ModelPose.Transforms[0].Translation);
        Assert.Equal(["write-1", "mapped-1", "readers-1", "released-1", "write-2", "mapped-2"], events.ToArray());
        world.ReleaseQuery(bodyPoses);
    }

    private readonly struct RecordPredictedStep(ConcurrentQueue<string> events) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            JobHandle written = new PredictBodies(Vector3.Zero, 64).Schedule(in context, dependency);
            return WorldJobs.GetScheduler(context.World).Schedule(new RecordWrite(events, context.Number), written);
        }
        private readonly record struct RecordWrite(ConcurrentQueue<string> Events, long Number) : IJob
        {
            public void Execute() => Events.Enqueue($"write-{Number}");
        }
    }

    private readonly struct WaitForMappedPose(ReadPhysicsPose read, ManualResetEventSlim mapped, ManualResetEventSlim release,
        ConcurrentQueue<string> events, Scheduler scheduler) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            scheduler.Schedule(new MarkRead(context.Number, mapped, release, events, scheduler), read.Schedule(in context, dependency));
        private readonly record struct MarkRead(long Number, ManualResetEventSlim Mapped, ManualResetEventSlim Release,
            ConcurrentQueue<string> Events, Scheduler Scheduler) : IJob
        {
            public void Execute()
            {
                Assert.Same(Scheduler, JobSystem.Current);
                Events.Enqueue($"mapped-{Number}");
                if (Number == 1)
                {
                    Mapped.Set();
                    Release.Wait();
                    Events.Enqueue("released-1");
                }
            }
        }
    }

    private static Entity CreateBody(World world, Matrix4x4 transform)
    {
        Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out Vector3 position);
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = rotation });
        world.Add(body, new BodyMass { Motion = BodyMotion.Static });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        return body;
    }

    private static BoneTransform Transform(Vector3 translation, float radians) => new()
    {
        Translation = translation, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians), Scale = Vector3.One,
    };

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < .00002f, $"Expected {expected}; actual {actual}.");

    private readonly struct MovingSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation = new(time, 0, 0);
            destination[2].Translation = new(0, time, 0);
        }
    }

    private readonly struct MovingRootSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation = new(time, 0, 0);
        }
    }
}
