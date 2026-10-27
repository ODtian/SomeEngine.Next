using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed partial class PhysicsCrowdScene : IGameFramePhysics
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
        PhysicsSceneData? net = example.JointNet;
        bool jointNet = net is not null;
        int extraBodies = net?.Bodies.Length - 2 ?? 0;
        var characters = new Entity[count];
        var clothTargets = new Entity[count];
        var characterBodies = new ReadOnlyMemory<Entity>[count];
        var cloth = new List<Entity>(count * 16);
        var allBodies = new List<Entity>(count * bodiesPerCharacter + 1 + extraBodies);
        var worldDrivers = new List<Entity>();
        var parentDrivers = new List<Entity>();
        var drivers = new Entity[count];
        var storage = new PoseStorage(count * (CrowdExample.BoneCount + 16));
        var physics = Keep(new DevicePosePhysics(backend, device, world,
            new(count * bodiesPerCharacter + 1 + extraBodies, count * lengthsPerCharacter, count * bendsPerCharacter)));
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
        float netWidth = net is { } grid ? (float)(grid.Bodies[^1].Position[0] - grid.Bodies[1].Position[0]) : 0;
        Entity floor = world.CreateEntity(new BodyPose
            { Position = new(MathF.Max(30, netWidth * .5f), -.1f, count / 100f), Rotation = Quaternion.Identity });
        world.Add(floor, new BodyMass { Motion = BodyMotion.Static });
        world.Add(floor, new BodyVelocity()); world.Add(floor, new BodyStepState());
        world.Add(floor, new DeviceBodyBinding(physics.Bodies, count * bodiesPerCharacter));
        world.Add(floor, new BoxGeometry(new(MathF.Max(35, netWidth * .5f + 2), .1f, count / 100f + 2)));
        allBodies.Add(floor);
        var boxes = new List<Entity>(extraBodies + 1) { floor };
        var jointEntities = new List<Entity>(drivers);
        if (net is { } data) AddJointNet(data, physics.Bodies, floor, allBodies, boxes, jointEntities);
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
        var joints = Keep(new DeviceSceneJoints(backend, device, world, CollectionsMarshal.AsSpan(jointEntities)));
        var motors = Keep(new DeviceCrowdMotors(backend, device, world, drivers));
        var contacts = Keep(new DeviceCrowdContacts(backend, device, world, physics.Bodies, characterBodies, cloth, boxes,
            CollectionsMarshal.AsSpan(allBodies), net?.CollisionExclusions.Length ?? 0, jointNet, shaders));
        QueryHandle jointRows = Query(new QueryDefinitionBuilder().All<ScenePhysicsJoint>().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<DeviceConstraintBinding<DeviceJointConstraint>>()
            .Read<AvbdJointState>().Read<DeviceAvbdState<DeviceAvbdJointState>>());
        var sources = Keep(new DevicePoseSources(backend, device, world, targets,
            DevicePoseShaders.Get(device.Capabilities.CompileTarget),
            new(count * 2, count * 112, count * 2, count * 112, 1, 0, 1, count * 5), physics));
        DevicePoseBodyStages poses = sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
        var motorTargets = Keep(new DeviceCrowdMotorTargets(backend, device, world, characters, sources,
            physics.Bodies, joints.Data, motors.Data, shaders.CrowdMotorTargets()));
        var dynamics = Keep(new DeviceBodyDynamics(backend, device, world, members, physics.Bodies, shaders.CrowdBodies(), 64));
        var lengths = Keep(new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengthRows, physics.Lengths, new(physics.LengthMultipliers), "DeviceLengthsSelection"));
        var bends = Keep(new DeviceConstraintSelection<XpbdBend, DeviceBendConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, bendRows, physics.Bends, new(physics.BendMultipliers), "DeviceBendsSelection"));
        var bindings = new Dictionary<string, ResourceBinding[]>();
        physics.Lengths.AddBindings(bindings); physics.Bends.AddBindings(bindings);
        physics.LengthMultipliers.AddBindings(bindings); physics.BendMultipliers.AddBindings(bindings);
        joints.AddBindings(bindings); motors.AddBindings(bindings);
        contacts.AddBindings(bindings);
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        JobResourceAccess[] reads = [.. physics.Lengths.GetReadAccesses(scheduler), .. physics.Bends.GetReadAccesses(scheduler),
            .. joints.Data.GetReadAccesses(scheduler), .. motors.Data.GetReadAccesses(scheduler), .. contacts.GetReadAccesses(scheduler)];
        var graphSelections = new List<IDeviceConstraintSelection> { lengths, bends, motors.Selection };
        if (jointNet) graphSelections.Add(joints.Selection);
        graphSelections.Add(contacts.Selection);
        var graph = Keep(new DeviceConstraintGraph(backend, device, physics.Bodies, dynamics, shaders.CrowdGraph(jointNet),
            new(count * (lengthsPerCharacter + bendsPerCharacter + 1) + contacts.Parameters.Capacity
                + (jointNet ? joints.Data.Capacity : 0), 5), bindings, reads, 64, graphSelections.ToArray()));
        JobResourceAccess[] solverReads = [.. reads, physics.LengthMultipliers.GetReadAccess(scheduler),
            physics.BendMultipliers.GetReadAccess(scheduler), motors.States.GetWriteAccess(scheduler),
            contacts.Histories.GetWriteAccess(scheduler), contacts.Responses.GetWriteAccess(scheduler)];
        var solverSelections = new List<IDeviceConstraintSelection>
            { motors.StateSelection, contacts.StateSelection, contacts.ResponseSelection };
        if (jointNet)
        {
            solverReads = [.. solverReads, joints.States.GetWriteAccess(scheduler)];
            solverSelections.Add(joints.StateSelection);
        }
        var avbd = Keep(new DeviceAvbdSolver(backend, device, physics.Bodies, equations, graph, shaders.CrowdAvbd(jointNet),
            new(1, 1, .8f, 0), bindings, solverReads, 64,
            solverSelections.ToArray()));
        var xpbd = Keep(new DeviceXpbdSolver(backend, device, physics.Bodies, graph,
            shaders.CrowdXpbd(backend.TryGetCapability<IndirectCommands>(device, out _), jointNet), new(1, 64),
            bindings, reads, new[] { physics.LengthMultipliers.States, physics.BendMultipliers.States }, 64));
        var results = Keep(new DeviceBodyResults(backend, device, physics.Bodies));
        var response = Keep(new DeviceContactActions(backend, device, physics.Bodies, graph, contacts.Responses,
            shaders.CrowdResponses(jointNet), bindings, reads, 64, new IDeviceConstraintSelection[] { contacts.ResponseSelection }));
        var controls = Keep(new AdvanceCrowdTargets(world));
        var equationInputs = new CrowdEquationInputs(avbd);
        var constraintInputs = Keep(DeviceCrowdConstraintInputs.Create(world, members, lengthRows, bendRows, jointRows,
            physics.Lengths.BeginInputs().Then(writeLengths).Then(physics.Lengths.UploadInputs())
                .Then(physics.Bends.BeginInputs()).Then(writeBends).Then(physics.Bends.UploadInputs()).Then(joints.Upload())));
        PhysicsSceneTimings? timings = example.Timings;
        var upload = bodyInputs.Upload().Measure(timings, "Body component inputs")
            .Then(constraintInputs.Upload().Measure(timings, "Constraint component inputs"))
            .Then(motors.Upload().Measure(timings, "Motor component history"));
        var initial = new PhysicsStepContext(world, new QueryDefinitionBuilder().All<CrowdPhysicsBody>().Build(), -1, 0, 1, 0, 0);
        contacts.Parameters.BeginInputs().Then(contacts.Parameters.UploadInputs())
            .Then(bodyInputs.Upload()).Then(constraintInputs.Upload()).Then(motors.Upload())
            .Then(sources.Evaluate()).Then(motorTargets.WriteTargets()).Then(sources.CopyOutput())
            .Then(results.Readback(1024)).Schedule(in initial, default).Complete();
        var stages = controls.Measure(timings, "Scene inputs").Then(upload.Measure(timings, "Body and constraint inputs"))
            .Then(poses.WriteTargets().Measure(timings, "Pose sources and targets"))
            .Then(motorTargets.WriteTargets().Measure(timings, "Animation motor targets"))
            .Then(inputs.WriteInputs().Then(inputs.Apply()).Measure(timings, "Timed body inputs"))
            .Then(dynamics.Predict(new(0, -9.81f, 0)).Measure(timings, "Body prediction"))
            .Then(contacts.Detect().Measure(timings, "Geometry and persistent contacts"))
            .Then(response.Initialize().Measure(timings, "Contact response inputs"))
            .Then(graph.Build().Measure(timings, "Constraint graph"))
            .Then(new InitializeCrowdEquations(equationInputs).Measure(timings, "Body equations"))
            .Then(xpbd.Initialize(buildGraph: false).Measure(timings, "XPBD history"))
            .Then(new SolveCrowdConstraints(avbd.Iterate(1, buildGraph: false), xpbd.Iterate(1, buildGraph: false), 8, timings)
                .Measure(timings, "AVBD and XPBD"))
            .Then(dynamics.UpdateVelocities().Then(response.ApplyRestitution(1, buildGraph: false)).Measure(timings, "Velocities and restitution"))
            .Then(poses.MapBones().Then(sources.CopyOutput()).Measure(timings, "Bone output"));
        var consumer = results.Readback(1024).Measure(timings, "Body results");
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

internal sealed class CrowdEquationInputs(DeviceAvbdSolver solver)
{
    private bool _uploaded;
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        DeviceInputTransfer transfer = _uploaded ? DeviceInputTransfer.Resident : DeviceInputTransfer.Upload;
        _uploaded = true;
        return solver.Initialize(buildGraph: false, equationInputs: transfer).Schedule(in context, dependency);
    }
}
internal readonly struct InitializeCrowdEquations(CrowdEquationInputs inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}

internal readonly struct SolveCrowdConstraints(IterateDeviceAvbd avbd, IterateDeviceXpbdGraph xpbd, int iterations,
    PhysicsSceneTimings? timings) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            dependency = avbd.Measure(timings, "AVBD iteration").Schedule(in context, dependency);
            dependency = xpbd.Measure(timings, "XPBD iteration").Schedule(in context, dependency);
        }
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
