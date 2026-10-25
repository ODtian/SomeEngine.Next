using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PhysicsMotorTargetBehaviorTests
{
    [Fact]
    public void AvbdMotorFollowsMappedAnimationTargetsAtEachPhysicalSubstep()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var skeleton = new Skeleton([-1], [new BoneTransform { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<Skeleton, MovingSampler>(skeleton));
        var request = new PosePredictionRequest(PosePath.FromClip(clip), new PoseTimeOrigin(0));
        var sampling = new PosePredictionBatch([new(request, [0]), new(request, [0])]);
        var mapping = new PhysicsPoseMap(skeleton, [new(0, Matrix4x4.CreateTranslation(0, 1, 0))]);
        Matrix4x4 actor = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0);
        var targets = new AnimationMotorTargets(sampling, mapping.Bodies[0], actor);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(9, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        var joint = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(joint.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(0, 100, 0), LinearDamping = new Vector3(0, 10, 0), MaximumForce = new Vector3(100),
        });
        world.Add(joint.Entity, new JointMotorTarget { Frame = JointFrame.Identity });
        world.Add(joint.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().ReadWrite<JointMotorTarget>().ReadWrite<AvbdMotorState>());
        var storage = new PoseStorage();
        using SkeletonInstance output = storage.CreateInstance(skeleton);
        Entity poseTarget = PoseAnimation.CreateTarget(world, output, resultCount: 2, output: new(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.Leaf<ClipParameters, ClipLeaf>(poseTarget, new(clip, new(0), Loop: true));
            builder.BlendPhysicsPose(poseTarget, new(new(0), new(1), mapping, new BoneMask([1])));
        }
        Entity animation = builder.Play();
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        var binding = (Body: target, Actor: actor, Query: poses, Read: default(ReadPhysicsPose));
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity Body, Matrix4x4 Actor, QueryHandle Query, ReadPhysicsPose Read)>(
            animation, ref binding, static (inputs, ref binding) =>
            {
                inputs[0].ActorTransform = binding.Actor;
                binding.Read = inputs[0].BindBodies(binding.Query, [binding.Body]);
            });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveAvbd.Create(world, bodies, new AnimatedAvbdMotor(motors, targets), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));
        using var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2)
            .CreateFrame(binding.Read, stepDuration: .2, maximumDelay: 0, firstStepNumber: 0, startTime: 0);

        physics.AdvanceTime(.2);
        physics.CompleteForRead();
        var frame = new ImmediateSystemContext(world, 0, 0, .2);
        systems.Update(ref frame);

        Assert.InRange(Vector3.Distance(new Vector3(9, 0.1666667f, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        JointMotorTarget sampled = world.Read<JointMotorTarget>(joint.Entity);
        Assert.Equal(0.2, sampled.Time, 10);
        Assert.InRange(Vector3.Distance(new Vector3(9, 0.2f, 0), sampled.Frame.Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(.1666667f, 0, 0), output.ModelPose.Transforms[0].Translation), 0, 1e-5f);
        Assert.Equal(.2, output.ModelPose.SampleTime, 6);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void XpbdMotorFollowsMappedAnimationTargetsAtEachPhysicalSubstep()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var skeleton = new Skeleton([-1], [new BoneTransform { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<Skeleton, MovingSampler>(skeleton));
        var request = new PosePredictionRequest(PosePath.FromClip(clip), new PoseTimeOrigin(0));
        var sampling = new PosePredictionBatch([new(request, [0]), new(request, [0])]);
        var mapping = new PhysicsPoseMap(skeleton, [new(0, Matrix4x4.CreateTranslation(0, 1, 0))]);
        Matrix4x4 actor = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0);
        var targets = new AnimationMotorTargets(sampling, mapping.Bodies[0], actor);
        Entity source = CreateBody(world, new BodyPose { Rotation = Quaternion.Identity }, BodyMotion.Static);
        Entity target = CreateBody(world, new BodyPose
        {
            Position = new Vector3(9, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        }, BodyMotion.Dynamic);
        var joint = world.CreateRelation(source, target, new JointConstraint
        {
            SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity,
        });
        world.Add(joint.Entity, new JointMotor
        {
            Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new Vector3(0, 100, 0), LinearDamping = new Vector3(0, 10, 0), MaximumForce = new Vector3(100),
        });
        world.Add(joint.Entity, new JointMotorTarget { Frame = JointFrame.Identity });
        world.Add(joint.Entity, new XpbdMotorState());
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<JointMotor>().ReadWrite<JointMotorTarget>().ReadWrite<XpbdMotorState>());
        var stages = new PredictBodies(Vector3.Zero, 64)
            .Then(SolveXpbd.Create(world, bodies, new AnimatedXpbdMotor(motors, targets), 32))
            .Then(new UpdateBodyVelocities(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 2);

        physics.Schedule(0, 0, 0.2).Complete();

        Assert.InRange(Vector3.Distance(new Vector3(9, 0.1666667f, 0), world.Read<BodyPose>(target).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        JointMotorTarget sampled = world.Read<JointMotorTarget>(joint.Entity);
        Assert.Equal(0.2, sampled.Time, 10);
        Assert.InRange(Vector3.Distance(new Vector3(9, 0.2f, 0), sampled.Frame.Position), 0, 1e-5f);
        world.ReleaseQuery(motors);
        world.ReleaseQuery(bodies);
    }

    private sealed class AnimationMotorTargets(PosePredictionBatch sampling, PhysicsBone body, Matrix4x4 actor)
    {
        private readonly double[] _times = new double[2];
        private readonly Matrix4x4[] _transforms = new Matrix4x4[2];
        private readonly PosePredictionSample[] _samples = new PosePredictionSample[2];

        internal void WriteTarget(in PhysicsStepContext context, QueryHandle query)
        {
            _times[0] = context.StartTime;
            _times[1] = context.EndTime;
            sampling.SampleModel(_times, context.EndTime, _transforms, _samples);
            Matrix4x4 first = body.BodyInBone * _transforms[0] * actor;
            Matrix4x4 last = body.BodyInBone * _transforms[1] * actor;
            Matrix4x4.Decompose(last, out _, out Quaternion rotation, out Vector3 position);
            var target = new JointMotorTarget
            {
                Frame = new JointFrame(position, rotation),
                LinearVelocity = (last.Translation - first.Translation) / (float)context.Duration,
                Time = _samples[1].Time,
            };
            context.World.ExecuteQuery(query, ref target, static (scoped QueryCursor rows, ref JointMotorTarget target) =>
            {
                foreach (QueryRow row in rows.Rows) row.ReadWrite<JointMotorTarget>() = target;
            });
        }
    }

    private readonly struct AnimatedAvbdMotor : IAvbdConstraints
    {
        private readonly QueryHandle _query;
        private readonly AnimationMotorTargets _targets;
        private readonly AvbdConstraints<JointConstraint, AvbdJointMotorMath> _motors;

        internal AnimatedAvbdMotor(QueryHandle query, AnimationMotorTargets targets)
        {
            _query = query;
            _targets = targets;
            _motors = new(query);
        }

        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            _motors.AddAccesses(world, accesses);
            accesses.Add(JobResourceAccess.Exclusive(WorldJobs.GetScheduler(world).GetResourceToken(_targets)));
        }

        public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
        {
            _targets.WriteTarget(in context, _query);
            _motors.Initialize(in context, bodies, retention);
        }

        public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
            float initialErrorFraction, ref AvbdBodyEquation equation) =>
            _motors.AccumulateBodyEquation(in context, body, bodies, initialErrorFraction, ref equation);

        public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction) =>
            _motors.UpdateDuals(in context, bodies, initialErrorFraction);
    }

    private readonly struct AnimatedXpbdMotor : IXpbdConstraints
    {
        private readonly QueryHandle _query;
        private readonly AnimationMotorTargets _targets;
        private readonly XpbdConstraints<JointConstraint, XpbdJointMotorProjection> _motors;

        internal AnimatedXpbdMotor(QueryHandle query, AnimationMotorTargets targets)
        {
            _query = query;
            _targets = targets;
            _motors = new(query);
        }

        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            _motors.AddAccesses(world, accesses);
            accesses.Add(JobResourceAccess.Exclusive(WorldJobs.GetScheduler(world).GetResourceToken(_targets)));
        }

        public void Initialize(in PhysicsStepContext context)
        {
            _targets.WriteTarget(in context, _query);
            _motors.Initialize(in context);
        }

        public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies) =>
            _motors.Project(in context, bodies);
    }

    private static Entity CreateBody(World world, BodyPose pose, BodyMotion motion)
    {
        Entity body = world.CreateEntity(pose);
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity());
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }

    private readonly struct MovingSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation = new Vector3(time, 0, 0);
        }
    }
}
