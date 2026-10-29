using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.Pose;

public readonly record struct DevicePosePhysicsCapacity(int Bodies, int Lengths, int Bends);
public readonly record struct GpuPoseBody(Entity Target, int RigStep, int Bone) : IComponent;
public readonly record struct GpuPoseChain(Entity Target, GpuPoseRig Rig, int RigStep, GpuXpbdChain Chain) : IComponent;
[BufferCapacity(4)]
public readonly record struct GpuPoseLengthLink(RelationEdge<XpbdLength> Length) : IBufferElement;
[BufferCapacity(4)]
public readonly record struct GpuPoseBendLink(Entity Bend) : IBufferElement;
internal readonly struct GpuPoseLength : IComponent;
internal readonly struct GpuPoseBend : IComponent;

/// <summary>The original rig's entity bodies, typed constraints and selected device representations.</summary>
public sealed partial class DevicePosePhysics : IDisposable
{
    private readonly World _world;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly QueryHandle _members, _lengthRows, _bendRows, _chainRows;
    private readonly DeviceBodyInputs _inputs;
    private readonly WriteDeviceConstraintInputs<XpbdLength, DeviceLengthConstraint, DeviceLengthSource> _writeLengths;
    private readonly WriteDeviceConstraintInputs<XpbdBend, DeviceBendConstraint, DeviceBendSource> _writeBends;
    private readonly Dictionary<(Entity Target, int RigStep), Entity> _chains = [];
    private readonly Stack<int> _freeBodies = [], _freeLengths = [], _freeBends = [];
    private readonly Buffer _sourceModels;
    private readonly BufferUav _bodyView, _sourceView;
    private readonly JobResourceAccess[] _chainAccesses;
    private int _bodyCount, _lengthCount, _bendCount;
    private bool _initialized;

    public DevicePosePhysics(IGraphicsBackend backend, Device device, World world, DevicePosePhysicsCapacity capacity)
    {
        _world = world; _backend = backend; _device = device;
        Bodies = new(backend, device, capacity.Bodies);
        Lengths = new(backend, device, capacity.Lengths, "DeviceLengths");
        Bends = new(backend, device, capacity.Bends, "DeviceBends");
        LengthMultipliers = new(backend, device, capacity.Lengths, "DeviceLengthMultipliers");
        BendMultipliers = new(backend, device, capacity.Bends, "DeviceBendMultipliers");
        _sourceModels = backend.CreateBuffer(device, new((ulong)capacity.Bodies * 64,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Pose source bone frames"), MemoryType.DeviceLocal);
        _sourceView = backend.CreateBufferUav(device, new(_sourceModels, new(0, _sourceModels.Info.Size), StructureStride: 64));
        _bodyView = backend.CreateBufferUav(device, new(Bodies.States, new(0, Bodies.States.Info.Size), StructureStride: DeviceBodies.StateStride));
        _members = world.Query(new QueryDefinitionBuilder().All<GpuPoseBody>().Read<DeviceBodyBinding>());
        _lengthRows = world.Query(new QueryDefinitionBuilder().All<GpuPoseLength>().Read<XpbdLength>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>().Read<DeviceConstraintBinding<DeviceLengthConstraint>>());
        _bendRows = world.Query(ConstraintBodies.AddReads(new QueryDefinitionBuilder().All<GpuPoseBend>().Read<XpbdBend>()
            .Read<DeviceConstraintBinding<DeviceBendConstraint>>().Build()));
        QueryDefinition chainDefinition = QueryDefinition.Empty.WithAccessFrom(ConstraintBodies.AddReads(new QueryDefinitionBuilder().Read<GpuPoseChain>()
            .ReadBuffer<GpuPoseLengthLink>().ReadBuffer<GpuPoseBendLink>().Optional<DeviceBodyBinding>(QueryAccess.Read)
            .Optional<DeviceConstraintBinding<DeviceLengthConstraint>>(QueryAccess.Read)
            .Optional<DeviceConstraintBinding<DeviceBendConstraint>>(QueryAccess.Read).Build()));
        _chainRows = world.Query(chainDefinition);
        var resources = new QueryReadResources();
        resources.Add<GpuPoseChain, GpuPoseChainResources>();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceConstraintBinding<DeviceLengthConstraint>, DeviceConstraintReferenceResources<DeviceLengthConstraint>>();
        resources.Add<DeviceConstraintBinding<DeviceBendConstraint>, DeviceConstraintReferenceResources<DeviceBendConstraint>>();
        _chainAccesses = new JobResourceAccess[QueryJobAccess.Count(chainDefinition)];
        QueryJobAccess.CopyAccesses(world, chainDefinition, _chainAccesses, resources);
        _inputs = new(world, _members, Bodies);
        _writeLengths = Lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, _lengthRows, default, 64);
        _writeBends = Bends.WriteInputs<XpbdBend, DeviceBendSource>(world, _bendRows, default, 64);
    }

    public DeviceBodies Bodies { get; }
    public DeviceConstraintData<DeviceLengthConstraint> Lengths { get; }
    public DeviceConstraintData<DeviceBendConstraint> Bends { get; }
    public DeviceConstraintState<float> LengthMultipliers { get; }
    public DeviceConstraintState<float> BendMultipliers { get; }
    public UploadDevicePosePhysics Upload() => new(this);
    internal bool HasRig(Entity target, GpuPoseRig rig) =>
        _chains.TryGetValue((target, FirstChain(rig)), out Entity chain)
        && ReferenceEquals(_world.Read<GpuPoseChain>(chain).Rig, rig);

    /// <summary>Creates the actual entities for each secondary-chain step in this target's current rig.</summary>
    public void Bind(Entity target)
    {
        GpuPoseTarget input = _world.Read<GpuPoseTarget>(target);
        if (_chains.TryGetValue((target, FirstChain(input.Rig!)), out Entity existing)
            && ReferenceEquals(_world.Read<GpuPoseChain>(existing).Rig, input.Rig)) return;
        Remove(target);
        for (int step = 0; step < input.Rig!.Steps.Length; step++)
        {
            if (input.Rig.Steps[step] is not GpuXpbdPose pose) continue;
            GpuXpbdChain definition = pose.Chain;
            var bodies = new Entity[definition.Bones.Length];
            for (int point = 0; point < bodies.Length; point++)
            {
                GpuXpbdBone bone = definition.Bones[point];
                Entity body = _world.CreateEntity(new GpuPoseBody(target, step, bone.Bone));
                _world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
                _world.Add(body, new BodyMass { Motion = bone.InverseMass == 0 ? BodyMotion.Kinematic : BodyMotion.Dynamic,
                    InverseMass = bone.InverseMass });
                _world.Add(body, new BodyVelocity()); _world.Add(body, new BodyStepState());
                _world.Add(body, new DeviceBodyBinding(Bodies, Allocate(_freeBodies, ref _bodyCount)));
                bodies[point] = body;
            }
            Entity chain = ConstraintBodies.Create(_world, new GpuPoseChain(target, input.Rig, step, definition), bodies);
            _world.AddBuffer<GpuPoseLengthLink>(chain); _world.AddBuffer<GpuPoseBendLink>(chain);
            for (int point = 1; point < bodies.Length; point++)
            {
                float rest = definition.Skeleton.ReferencePose[definition.Bones[point].Bone].Translation.Length();
                RelationEdge<XpbdLength> length = _world.CreateRelation(bodies[point - 1], bodies[point],
                    new XpbdLength { RestLength = rest, Compliance = definition.LengthCompliance });
                _world.Add(length.Entity, new GpuPoseLength());
                _world.Add(length.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(Lengths, Allocate(_freeLengths, ref _lengthCount)));
                _world.Add(length.Entity, new DeviceXpbdState<float>(LengthMultipliers));
                var link = new GpuPoseLengthLink(length);
                _world.ExecuteBufferWrite<GpuPoseLengthLink, GpuPoseLengthLink>(chain, ref link, static (links, ref link) => links.Add(link));
            }
            for (int point = 1; point + 1 < bodies.Length; point++)
            {
                Entity bend = ConstraintBodies.Create(_world, new XpbdBend { MinimumCosine = MathF.Cos(definition.MaximumBend),
                    Compliance = definition.BendCompliance }, bodies.AsSpan(point - 1, 3));
                _world.Add(bend, new GpuPoseBend());
                _world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(Bends, Allocate(_freeBends, ref _bendCount)));
                _world.Add(bend, new DeviceXpbdState<float>(BendMultipliers));
                var link = new GpuPoseBendLink(bend);
                _world.ExecuteBufferWrite<GpuPoseBendLink, GpuPoseBendLink>(chain, ref link, static (links, ref link) => links.Add(link));
            }
            _chains.Add((target, step), chain);
        }
    }

    public ReadOnlyMemory<Entity> GetBodies(Entity target, int rigStep)
    {
        var capture = new ChainBodies(_chains[(target, rigStep)]);
        _world.ExecuteReadQuery(_chainRows, ref capture, static (scoped QueryCursor rows, ref ChainBodies capture) =>
        {
            rows.TryGetRow(capture.Chain, out QueryRow row);
            ReadOnlySpan<ConstraintBodyLink> links = row.ReadBuffer<ConstraintBodyLink>().AsSpan();
            capture.Bodies = new Entity[links.Length];
            for (int point = 0; point < links.Length; point++) capture.Bodies[point] = ConstraintBodies.GetBody(in links[point], rows);
        });
        return capture.Bodies;
    }

    public void Remove(Entity target)
    {
        foreach (var entry in _chains.Where(entry => entry.Key.Target == target).ToArray())
        {
            Entity chain = entry.Value;
            Entity[] bodies = GetBodies(target, entry.Key.RigStep).ToArray();
            Entity[] lengths = [], bends = [];
            _world.ExecuteBufferRead<GpuPoseLengthLink, Entity[]>(chain, ref lengths, (links, ref values) =>
            {
                values = new Entity[links.Count];
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = links.AsSpan()[index].Length.Entity;
                }
            });
            _world.ExecuteBufferRead<GpuPoseBendLink, Entity[]>(chain, ref bends, (links, ref values) =>
            {
                values = new Entity[links.Count];
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = links.AsSpan()[index].Bend;
                }
            });
            foreach (Entity length in lengths)
            { _freeLengths.Push(_world.Read<DeviceConstraintBinding<DeviceLengthConstraint>>(length).Index); _world.DestroyEntity(length); }
            foreach (Entity bend in bends)
            { _freeBends.Push(_world.Read<DeviceConstraintBinding<DeviceBendConstraint>>(bend).Index); _world.DestroyEntity(bend); }
            _world.DestroyEntity(chain);
            foreach (Entity body in bodies)
            { _freeBodies.Push(_world.Read<DeviceBodyBinding>(body).Index); _world.DestroyEntity(body); }
            _chains.Remove(entry.Key);
        }
    }

    private static int FirstChain(GpuPoseRig rig)
    { for (int step = 0; step < rig.Steps.Length; step++) if (rig.Steps[step] is GpuXpbdPose) return step; return -1; }
    private static int Allocate(Stack<int> free, ref int count) => free.TryPop(out int index) ? index : count++;
    private struct ChainBodies(Entity chain)
    {
        internal readonly Entity Chain = chain;
        internal Entity[] Bodies = [];
    }

    internal JobResourceAccess[] CaptureAccesses => _chainAccesses;
    internal void RecordFirstUse(CommandContext commands)
    {
        if (_initialized) return;
        foreach (Buffer buffer in new[] { LengthMultipliers.States, BendMultipliers.States, _sourceModels })
            _backend.Barrier(commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        _initialized = true;
    }
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(_bodyView)]);
        bindings.Add("GpuPosePhysicsSources", [ResourceBinding.WritableBuffer(_sourceView)]);
        Lengths.AddBindings(bindings); Bends.AddBindings(bindings);
        LengthMultipliers.AddBindings(bindings); BendMultipliers.AddBindings(bindings);
    }
    internal JobResourceAccess[] GetDeviceAccesses(Scheduler scheduler) =>
        [Bodies.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(Lengths.Data), 0, (long)Lengths.Data.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Lengths.Metadata), 0, (long)Lengths.Metadata.Info.Size),
            .. Bends.GetReadAccesses(scheduler), LengthMultipliers.GetWriteAccess(scheduler), BendMultipliers.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_sourceModels), 0, (long)_sourceModels.Info.Size)];
    internal JobResourceAccess[] GetBoneMappingAccesses(Scheduler scheduler) =>
        [Bodies.GetReadAccess(scheduler), JobResourceAccess.Read(scheduler.GetResourceToken(_sourceModels), 0, (long)_sourceModels.Info.Size)];

    internal JobHandle ScheduleUpload(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = _inputs.Upload().Schedule(in context, dependency);
        dependency = Lengths.BeginInputs().Schedule(in context, dependency);
        dependency = _writeLengths.Schedule(in context, dependency);
        dependency = Lengths.UploadInputs().Schedule(in context, dependency);
        dependency = Bends.BeginInputs().Schedule(in context, dependency);
        dependency = _writeBends.Schedule(in context, dependency);
        return Bends.UploadInputs().Schedule(in context, dependency);
    }
    public void Dispose()
    {
        foreach (Entity target in _chains.Keys.Select(key => key.Target).Distinct().ToArray()) Remove(target);
        _inputs.Dispose(); _writeLengths.Dispose(); _writeBends.Dispose();
        _world.ReleaseQuery(_chainRows); _world.ReleaseQuery(_bendRows); _world.ReleaseQuery(_lengthRows); _world.ReleaseQuery(_members);
        _bodyView.Dispose(); _sourceView.Dispose(); _sourceModels.Dispose();
        _copyCommands?.Dispose();
        BendMultipliers.Dispose(); LengthMultipliers.Dispose(); Bends.Dispose(); Lengths.Dispose(); Bodies.Dispose();
    }
}

public readonly struct UploadDevicePosePhysics(DevicePosePhysics physics) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => physics.ScheduleUpload(in context, dependency);
}

/// <summary>Reads the rig's shared immutable definitions during body-reference capture.</summary>
public readonly struct GpuPoseChainResources : IJobComponentResources<GpuPoseChain>
{
    public static void Collect(in GpuPoseChain component, ref JobResourceAccessBuilder resources) { }
}
