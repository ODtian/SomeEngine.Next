using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>Character and cloth entity references sharing one device body step and the ordinary pose outputs.</summary>
public sealed class PhysicsCrowdScene : IGameFramePhysics
{
    private readonly World _world;
    private readonly List<IDisposable> _resources = [];
    private readonly List<QueryHandle> _queries = [];
    private readonly List<SkeletonInstance> _skeletons = [];
    private readonly IGameFramePhysics _frame;
    private readonly Func<PhysicsStepResult> _complete;

    internal PhysicsCrowdScene(PhysicsCrowdExample example, IGraphicsBackend backend, Device device, World world)
    {
        _world = world;
        int count = example.CharacterCount;
        const int bodiesPerCharacter = 20, lengthsPerCharacter = 45, bendsPerCharacter = 18;
        var characters = new Entity[count];
        var clothTargets = new Entity[count];
        var characterBodies = new ReadOnlyMemory<Entity>[count];
        var cloth = new List<Entity>(count * 16);
        var allBodies = new List<Entity>(count * bodiesPerCharacter + 1);
        var worldDrivers = new List<Entity>();
        var parentDrivers = new List<Entity>();
        var drivers = new Entity[count];
        var storage = new PoseStorage(count * (CrowdExample.BoneCount + 16));
        var physics = Keep(new DevicePosePhysics(backend, device, world,
            new(count * bodiesPerCharacter + 1, count * lengthsPerCharacter, count * bendsPerCharacter)));
        var equations = Keep(new DeviceAvbdBodyEquations(backend, device, physics.Bodies));
        int extraLength = count * 15, extraBend = count * 10;
        for (int index = 0; index < count; index++)
        {
            Vector3 origin = new((index % 50) * 1.2f, 0, (index / 50) * 1.2f);
            SkeletonInstance character = storage.CreateInstance(example.Character.Skeleton);
            _skeletons.Add(character);
            characters[index] = CreateTarget(character, example.CharacterRig, origin, index, count);
            physics.Bind(characters[index]);
            Entity[] arm = physics.GetBodies(characters[index], 0).ToArray();
            characterBodies[index] = arm;
            allBodies.AddRange(arm);
            SkeletonInstance clothInstance = storage.CreateInstance(example.ClothSkeleton, character);
            _skeletons.Add(clothInstance);
            clothTargets[index] = CreateTarget(clothInstance, example.ClothRig, origin, index, count);
            physics.Bind(clothTargets[index]);
            var nodes = new Entity[16];
            for (int column = 0; column < 4; column++)
                physics.GetBodies(clothTargets[index], column).Span.CopyTo(nodes.AsSpan(column * 4, 4));
            cloth.AddRange(nodes); allBodies.AddRange(nodes);
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 4; row++)
                AddLength(nodes[column * 4 + row], nodes[(column + 1) * 4 + row], .2f);
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
            {
                AddLength(nodes[column * 4 + row], nodes[(column + 1) * 4 + row + 1], MathF.Sqrt(.08f));
                AddLength(nodes[column * 4 + row + 1], nodes[(column + 1) * 4 + row], MathF.Sqrt(.08f));
            }
            for (int column = 0; column < 2; column++)
            for (int row = 0; row < 4; row++)
            {
                Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f, Compliance = 1e-5f },
                    new[] { nodes[column * 4 + row], nodes[(column + 1) * 4 + row], nodes[(column + 2) * 4 + row] });
                world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(physics.Bends, extraBend++));
                world.Add(bend, new DeviceXpbdState<float>(physics.BendMultipliers));
            }
            JointMotorSpace space = index % 2 == 0 ? JointMotorSpace.World : JointMotorSpace.Source;
            Entity motor = world.CreateRelation(arm[0], arm[2], new JointConstraint
                { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity }).Entity;
            world.Add(motor, new ScenePhysicsJoint());
            world.Add(motor, new JointMotor { Space = space, Response = JointMotorResponse.Target,
                LinearStiffness = new(50), LinearDamping = new(5), MaximumForce = new(100) });
            Vector3 hand = character.ModelPose.Transforms[22].Translation;
            Vector3 root = character.ModelPose.Transforms[20].Translation;
            Vector3 goal = space == JointMotorSpace.World ? hand + origin : hand - root;
            world.Add(motor, new CrowdMotorTarget(goal));
            world.Add(motor, new JointMotorTarget { Frame = new(goal, Quaternion.Identity) });
            world.Add(motor, new AvbdJointState());
            world.Add(motor, new AvbdMotorState { LinearPenalty = new(100) });
            drivers[index] = motor;
            (space == JointMotorSpace.World ? worldDrivers : parentDrivers).Add(motor);
        }
        Entity floor = world.CreateEntity(new BodyPose { Position = new(30, -.1f, count / 100f), Rotation = Quaternion.Identity });
        world.Add(floor, new BodyMass { Motion = BodyMotion.Static });
        world.Add(floor, new BodyVelocity()); world.Add(floor, new BodyStepState());
        world.Add(floor, new DeviceBodyBinding(physics.Bodies, count * bodiesPerCharacter));
        world.Add(floor, new BoxGeometry(new(35, .1f, count / 100f + 2)));
        allBodies.Add(floor);
        foreach (Entity body in allBodies)
        {
            world.Add(body, new CrowdPhysicsBody());
            int bodyIndex = world.Read<DeviceBodyBinding>(body).Index;
            world.Add(body, new DeviceAvbdEquationBinding(equations, bodyIndex));
        }
        Entity perturbed = characterBodies[^1].Span[^1];
        world.AddBuffer<BodyImpulse>(perturbed);
        var impulse = new BodyImpulse { Linear = new(0, 0, 3), Time = 0 };
        world.ExecuteBufferWrite<BodyImpulse, BodyImpulse>(perturbed, ref impulse,
            static (buffer, ref value) => buffer.Add(value));
        CharacterTargets = characters; ClothTargets = clothTargets; CharacterBodies = characterBodies;
        Bodies = allBodies; ClothBodies = cloth; WorldDrivers = worldDrivers; ParentDrivers = parentDrivers;

        var shaders = new PhysicsSceneShaders(device.Capabilities.CompileTarget, Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        QueryHandle targets = Query(new QueryDefinitionBuilder().All<CrowdPose>().Read<GpuPoseTarget>());
        QueryHandle members = Query(new QueryDefinitionBuilder().All<CrowdPhysicsBody>().Read<DeviceBodyBinding>());
        QueryHandle lengthRows = Query(new QueryDefinitionBuilder().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>());
        QueryHandle bendRows = Query(ConstraintBodies.AddReads(new QueryDefinitionBuilder().Read<XpbdBend>()
            .Read<DeviceConstraintBinding<DeviceBendConstraint>>().Build()));
        var bodyInputs = Keep(new DeviceBodyInputs(world, members, physics.Bodies));
        var writeLengths = Keep(physics.Lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, lengthRows, default, 64));
        var writeBends = Keep(physics.Bends.WriteInputs<XpbdBend, DeviceBendSource>(world, bendRows, default, 64));
        var inputs = Keep(new DeviceBodyInputBuffers(backend, device, world, members, physics.Bodies, shaders.BodyInputs(), 64));
        var joints = Keep(new DeviceSceneJoints(backend, device, world, drivers));
        var motors = Keep(new DeviceCrowdMotors(backend, device, world, drivers));
        var sources = Keep(new DevicePoseSources(backend, device, world, targets,
            DevicePoseShaders.Get(device.Capabilities.CompileTarget),
            new(count * 2, count * 112, count * 2, count * 112, 1, 0, 1, count * 5), physics));
        DevicePoseBodyStages poses = sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
        var dynamics = Keep(new DeviceBodyDynamics(backend, device, world, members, physics.Bodies, shaders.CrowdBodies(), 64));
        var lengths = Keep(new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengthRows, physics.Lengths, new(physics.LengthMultipliers), "DeviceLengthsSelection"));
        var bends = Keep(new DeviceConstraintSelection<XpbdBend, DeviceBendConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, bendRows, physics.Bends, new(physics.BendMultipliers), "DeviceBendsSelection"));
        var bindings = new Dictionary<string, ResourceBinding[]>();
        physics.Lengths.AddBindings(bindings); physics.Bends.AddBindings(bindings);
        physics.LengthMultipliers.AddBindings(bindings); physics.BendMultipliers.AddBindings(bindings);
        joints.AddBindings(bindings); motors.AddBindings(bindings);
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        JobResourceAccess[] reads = [.. physics.Lengths.GetReadAccesses(scheduler), .. physics.Bends.GetReadAccesses(scheduler),
            .. joints.Data.GetReadAccesses(scheduler), .. motors.Data.GetReadAccesses(scheduler)];
        var graph = Keep(new DeviceConstraintGraph(backend, device, physics.Bodies, dynamics, shaders.CrowdGraph(),
            new(count * (lengthsPerCharacter + bendsPerCharacter + 1), 3), bindings, reads, 64,
            new IDeviceConstraintSelection[] { lengths, bends, motors.Selection }));
        JobResourceAccess[] solverReads = [.. reads, physics.LengthMultipliers.GetReadAccess(scheduler),
            physics.BendMultipliers.GetReadAccess(scheduler), motors.States.GetWriteAccess(scheduler)];
        var avbd = Keep(new DeviceAvbdSolver(backend, device, physics.Bodies, equations, graph, shaders.CrowdAvbd(),
            new(1, 1, .8f, 0), bindings, solverReads, 64, new IDeviceConstraintSelection[] { motors.StateSelection }));
        var xpbd = Keep(new DeviceXpbdSolver(backend, device, physics.Bodies, graph, shaders.CrowdXpbd(), new(1, 64),
            bindings, reads, new[] { physics.LengthMultipliers.States, physics.BendMultipliers.States }, 64));
        var results = Keep(new DeviceBodyResults(backend, device, physics.Bodies));
        var controls = Keep(new AdvanceCrowdTargets(world));
        var upload = bodyInputs.Upload().Then(physics.Lengths.BeginInputs()).Then(writeLengths).Then(physics.Lengths.UploadInputs())
            .Then(physics.Bends.BeginInputs()).Then(writeBends).Then(physics.Bends.UploadInputs()).Then(joints.Upload()).Then(motors.Upload());
        var initial = new PhysicsStepContext(world, new QueryDefinitionBuilder().All<CrowdPhysicsBody>().Build(), -1, 0, 1, 0, 0);
        upload.Then(sources.Evaluate()).Then(sources.CopyOutput()).Then(results.Readback(64)).Schedule(in initial, default).Complete();
        var stages = controls.Then(upload).Then(poses.WriteTargets()).Then(inputs.WriteInputs()).Then(inputs.Apply())
            .Then(dynamics.Predict(new(0, -9.81f, 0))).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new SolveCrowdConstraints(avbd.Iterate(1), xpbd.Iterate(1), 8)).Then(dynamics.UpdateVelocities())
            .Then(poses.MapBones()).Then(sources.CopyOutput());
        var consumer = results.Readback(64);
        var frame = PhysicsSimulation.Create(world, initial.Bodies, stages, 1).CreateFrame(consumer, StepSeconds, 0, 0, 0);
        _frame = frame; _complete = frame.CompleteForRead;

        void AddLength(Entity first, Entity second, float rest)
        {
            Entity length = world.CreateRelation(first, second, new XpbdLength { RestLength = rest, Compliance = 1e-6f }).Entity;
            world.Add(length, new DeviceConstraintBinding<DeviceLengthConstraint>(physics.Lengths, extraLength++));
            world.Add(length, new DeviceXpbdState<float>(physics.LengthMultipliers));
        }
    }

    public IReadOnlyList<Entity> CharacterTargets { get; }
    public IReadOnlyList<Entity> ClothTargets { get; }
    public IReadOnlyList<ReadOnlyMemory<Entity>> CharacterBodies { get; }
    public IReadOnlyList<Entity> Bodies { get; }
    public IReadOnlyList<Entity> ClothBodies { get; }
    public IReadOnlyList<Entity> WorldDrivers { get; }
    public IReadOnlyList<Entity> ParentDrivers { get; }
    public double StepSeconds => 1d / 60;
    public JobHandle Completion => _frame.Completion;
    public void AdvanceTime(double deltaTime) => _frame.AdvanceTime(deltaTime);
    public void SubmitDueSteps() => _frame.SubmitDueSteps();
    public JobHandle ConsumeCompleted() => _frame.ConsumeCompleted();
    public PhysicsStepResult CompleteForRead() => _complete();

    private T Keep<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
    private QueryHandle Query(QueryDefinitionBuilder definition) => Query(definition.Build());
    private QueryHandle Query(QueryDefinition definition) { QueryHandle query = _world.Query(definition); _queries.Add(query); return query; }
    private Entity CreateTarget(SkeletonInstance skeleton, GpuPoseRig rig, Vector3 origin, int character, int characters)
    {
        Entity entity = GpuPoseAnimation.CreateTarget(_world, skeleton);
        GpuPoseTarget input = _world.Read<GpuPoseTarget>(entity);
        input.Blend = rig.Source; input.Rig = rig; input.ModelToWorld = Matrix4x4.CreateTranslation(origin);
        input.SampleTime = input.PlaybackTime = character * (2f / characters); input.InputNumber = 1; input.Playing = true;
        _world.Replace(entity, input); _world.Add(entity, new CrowdPose(input.SampleTime));
        return entity;
    }
    public void Dispose()
    {
        _frame.Dispose();
        for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        foreach (QueryHandle query in _queries) _world.ReleaseQuery(query);
        for (int index = _skeletons.Count - 1; index >= 0; index--) _skeletons[index].Dispose();
    }
}

internal readonly struct CrowdPhysicsBody : IComponent;
internal readonly record struct CrowdPose(float Phase) : IComponent;
internal readonly record struct CrowdMotorTarget(Vector3 Position) : IComponent;

internal readonly struct SolveCrowdConstraints(IterateDeviceAvbd avbd, IterateDeviceXpbdGraph xpbd, int iterations) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        { dependency = avbd.Schedule(in context, dependency); dependency = xpbd.Schedule(in context, dependency); }
        return dependency;
    }
}

internal readonly struct AdvanceCrowdTargets : IPhysicsStage, IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _sources;
    private readonly JobResourceAccess[] _accesses;
    internal AdvanceCrowdTargets(World world)
    {
        _world = world;
        QueryDefinition definition = new QueryDefinitionBuilder().Read<CrowdPose>().Read<GpuPoseTarget>().Build();
        _sources = world.Query(definition);
        var resources = new QueryReadResources(); resources.Add<GpuPoseTarget, GpuPoseTargetResources>();
        _accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _accesses, resources);
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(_world, 1);
        commands.Schedule(0, new Advance(_world, _sources, context.EndTime), _accesses, dependency);
        return WorldJobs.GetScheduler(_world).ScheduleFinally(new Release(commands), default, commands.SchedulePlayback());
    }
    private readonly struct Advance(World world, QueryHandle targets, double time) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            var advance = new AdvanceRows(commands, time);
            world.ExecuteReadQuery(targets, ref advance, static (scoped QueryCursor rows, ref AdvanceRows advance) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    GpuPoseTarget input = row.Read<GpuPoseTarget>();
                    input.SampleTime = input.PlaybackTime = row.Read<CrowdPose>().Phase + (float)advance.Time;
                    input.CalculationTime = advance.Time; input.InputNumber++;
                    advance.Commands.Replace(row.Entity, in input);
                }
            });
        }
    }
    private ref struct AdvanceRows(JobCommandWriter commands, double time)
    {
        internal JobCommandWriter Commands = commands;
        internal readonly double Time = time;
    }
    private readonly struct Release(JobCommandBuffer commands) : IJob { public void Execute() => commands.Dispose(); }
    public void Dispose() => _world.ReleaseQuery(_sources);
}
