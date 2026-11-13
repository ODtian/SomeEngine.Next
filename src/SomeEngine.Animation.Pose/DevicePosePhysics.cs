using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
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
public readonly record struct GpuPoseLengthLink(RelationEdge<XpbdLength> Length) : IBufferElement;
public readonly record struct GpuPoseBendLink(Entity Bend) : IBufferElement;
internal readonly struct GpuPoseLength : IComponent;
internal readonly struct GpuPoseBend : IComponent;

/// <summary>The original rig's entity bodies, typed constraints and selected device representations.</summary>
public sealed partial class DevicePosePhysics : IDisposable
{
    private readonly World _world;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Query _members, _lengthRows, _bendRows, _chainRows;
    private readonly DeviceBodyInputs _inputs;
    private readonly WriteDeviceConstraintInputs<XpbdLength, DeviceLengthConstraint, DeviceLengthSource> _writeLengths;
    private readonly WriteDeviceConstraintInputs<XpbdBend, DeviceBendConstraint, DeviceBendSource> _writeBends;
    private readonly Dictionary<(Entity Target, int RigStep), Entity> _chains = [];
    private readonly Stack<int> _freeBodies = [], _freeLengths = [], _freeBends = [];
    private readonly Buffer _sourceModels;
    private readonly BufferUav _bodyView, _sourceView;
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
        _members = world.Query(new QueryDescription().With<GpuPoseBody>().With<DeviceBodyBinding>()
            .With<BodyPose>().With<BodyMass>().With<BodyVelocity>().With<BodyStepState>());
        _lengthRows = world.Query(new QueryDescription().With<GpuPoseLength>().With<XpbdLength>()
            .With<RelationEndpoints<XpbdLength>>().With<DeviceConstraintBinding<DeviceLengthConstraint>>());
        _bendRows = world.Query(ConstraintBodies.AddReads(new QueryDescription().With<GpuPoseBend>().With<XpbdBend>()
            .With<DeviceConstraintBinding<DeviceBendConstraint>>()));
        _chainRows = world.Query(ConstraintBodies.AddReads(new QueryDescription().With<GpuPoseChain>()
            .With<SomeEngine.ECS.Buffer<GpuPoseLengthLink>>().With<SomeEngine.ECS.Buffer<GpuPoseBendLink>>()));
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
        && ReferenceEquals(_world.ReadComponentData<GpuPoseChain>(chain).Rig, rig);

    /// <summary>Creates the actual entities for each secondary-chain step in this target's current rig.</summary>
    public void Bind(Entity target) => Bind(new ReadOnlySpan<Entity>(in target));

    public void Bind(ReadOnlySpan<Entity> targets)
    {
        using var structure = _world.OpenStructureWrite();
        var constraints = new List<Entity>();
        var offsets = new List<int> { 0 };
        var bodies = new List<Entity>();
        foreach (Entity target in targets) BindTarget(target, constraints, offsets, bodies);
        if (constraints.Count != 0)
            ConstraintBodies.Add(_world, CollectionsMarshal.AsSpan(constraints), CollectionsMarshal.AsSpan(offsets), CollectionsMarshal.AsSpan(bodies));
    }

    private void BindTarget(Entity target, List<Entity> constraints, List<int> offsets, List<Entity> linkedBodies)
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
            var indices = new int[bodies.Length];
            for (int point = 0; point < indices.Length; point++) indices[point] = Allocate(_freeBodies, ref _bodyCount);
            var rows = new BodyRows(target, step, definition, Bodies, indices, bodies);
            _world.ExecuteBundleSpawnBatch(BodyTypes, bodies.Length, rows, new BodyWriter());
            Entity chain = _world.Create(new GpuPoseChain(target, input.Rig, step, definition),
                default(SomeEngine.ECS.Buffer<GpuPoseLengthLink>), default(SomeEngine.ECS.Buffer<GpuPoseBendLink>));
            RecordBodies(chain, bodies, constraints, offsets, linkedBodies);
            using var lengthLinks = _world.GetBuffer<GpuPoseLengthLink>(chain);
            using var bendLinks = _world.GetBuffer<GpuPoseBendLink>(chain);
            for (int point = 1; point < bodies.Length; point++)
            {
                float rest = definition.Skeleton.ReferencePose[definition.Bones[point].Bone].Translation.Length();
                Entity length = _world.Create(new RelationEndpoints<XpbdLength>(bodies[point - 1], bodies[point]),
                    new XpbdLength { RestLength = rest, Compliance = definition.LengthCompliance }, new GpuPoseLength(),
                    new DeviceConstraintBinding<DeviceLengthConstraint>(Lengths, Allocate(_freeLengths, ref _lengthCount)),
                    new DeviceXpbdState<float>(LengthMultipliers));
                lengthLinks.Add(new(new(length)));
            }
            for (int point = 1; point + 1 < bodies.Length; point++)
            {
                Entity bend = _world.Create(new XpbdBend { MinimumCosine = MathF.Cos(definition.MaximumBend),
                    Compliance = definition.BendCompliance }, new GpuPoseBend(),
                    new DeviceConstraintBinding<DeviceBendConstraint>(Bends, Allocate(_freeBends, ref _bendCount)),
                    new DeviceXpbdState<float>(BendMultipliers));
                RecordBodies(bend, bodies.AsSpan(point - 1, 3), constraints, offsets, linkedBodies);
                bendLinks.Add(new(bend));
            }
            _chains.Add((target, step), chain);
        }
    }

    private static readonly int[] BodyTypes = [ComponentId<GpuPoseBody>.Value, ComponentId<BodyPose>.Value,
        ComponentId<BodyMass>.Value, ComponentId<BodyVelocity>.Value, ComponentId<BodyStepState>.Value, ComponentId<DeviceBodyBinding>.Value];
    private readonly record struct BodyRows(Entity Target, int Step, GpuXpbdChain Chain, DeviceBodies Bodies, int[] Indices, Entity[] Entities);
    private readonly struct BodyWriter : IBundleWriter<BodyRows>
    {
        public void Write(BundleWriteView view, in BodyRows rows)
        {
            GpuXpbdBone bone = rows.Chain.Bones[view.Index];
            view.Write(new GpuPoseBody(rows.Target, rows.Step, bone.Bone));
            view.Write(new BodyPose { Rotation = Quaternion.Identity });
            view.Write(new BodyMass { Motion = bone.InverseMass == 0 ? BodyMotion.Kinematic : BodyMotion.Dynamic, InverseMass = bone.InverseMass });
            view.Write(new BodyVelocity()); view.Write(new BodyStepState());
            view.Write(new DeviceBodyBinding(rows.Bodies, rows.Indices[view.Index]));
            rows.Entities[view.Index] = view.Entity;
        }
    }

    private static void RecordBodies(Entity constraint, ReadOnlySpan<Entity> source, List<Entity> constraints,
        List<int> offsets, List<Entity> bodies)
    {
        constraints.Add(constraint);
        foreach (Entity body in source) bodies.Add(body);
        offsets.Add(bodies.Count);
    }

    public ReadOnlyMemory<Entity> GetBodies(Entity target, int rigStep)
    {
        using RelationRead bodies = ConstraintBodies.GetBodies(_world, _chains[(target, rigStep)]);
        var entities = new Entity[bodies.Count];
        for (int index = 0; index < entities.Length; index++) entities[index] = bodies.Entries[index].Other;
        return entities;
    }

    public void Remove(Entity target)
    {
        using var structure = _world.OpenStructureWrite();
        foreach (var entry in _chains.Where(entry => entry.Key.Target == target).ToArray())
        {
            Entity chain = entry.Value;
            Entity[] bodies = GetBodies(target, entry.Key.RigStep).ToArray();
            Entity[] lengths, bends;
            using (var links = _world.ReadBuffer<GpuPoseLengthLink>(chain))
            {
                lengths = new Entity[links.Count];
                for (int index = 0; index < lengths.Length; index++) lengths[index] = links[index].Length.Entity;
            }
            using (var links = _world.ReadBuffer<GpuPoseBendLink>(chain))
            {
                bends = new Entity[links.Count];
                for (int index = 0; index < bends.Length; index++) bends[index] = links[index].Bend;
            }
            foreach (Entity length in lengths) _freeLengths.Push(_world.Read<DeviceConstraintBinding<DeviceLengthConstraint>>(length).Index);
            foreach (Entity bend in bends) _freeBends.Push(_world.Read<DeviceConstraintBinding<DeviceBendConstraint>>(bend).Index);
            foreach (Entity body in bodies) _freeBodies.Push(_world.Read<DeviceBodyBinding>(body).Index);
            _world.Destroy(lengths); _world.Destroy(bends); _world.Destroy(chain); _world.Destroy(bodies);
            _chains.Remove(entry.Key);
        }
    }

    private static int FirstChain(GpuPoseRig rig)
    { for (int step = 0; step < rig.Steps.Length; step++) if (rig.Steps[step] is GpuXpbdPose) return step; return -1; }
    private static int Allocate(Stack<int> free, ref int count) => free.TryPop(out int index) ? index : count++;
    private readonly struct ChainReads : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<GpuPoseChain>(hasResources: true), ChunkAccess.ReadBuffer<GpuPoseLengthLink>(),
                ChunkAccess.ReadBuffer<GpuPoseBendLink>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public static SystemAccesses AdditionalAccesses => new SystemAccesses()
            .ReadRandom<GpuPoseChain>().ReadRandom<SomeEngine.ECS.Buffer<GpuPoseLengthLink>>()
            .ReadRandom<SomeEngine.ECS.Buffer<GpuPoseBendLink>>()
            .ReadRandom<DeviceBodyBinding>().ReadRandom<DeviceConstraintBinding<DeviceLengthConstraint>>()
            .ReadRandom<DeviceConstraintBinding<DeviceBendConstraint>>();
        public void Execute(SystemChunk rows) { }
    }
    internal JobResourceAccess[] CaptureAccesses => ((WorldJobs)_world.Execution!).CollectAccess<ChainReads>(_chainRows);
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
        dependency = _writeLengths.Schedule(in context, dependency);
        dependency = Lengths.UploadInputs().Schedule(in context, dependency);
        dependency = _writeBends.Schedule(in context, dependency);
        return Bends.UploadInputs().Schedule(in context, dependency);
    }
    public void Dispose()
    {
        foreach (Entity target in _chains.Keys.Select(key => key.Target).Distinct().ToArray()) Remove(target);
        _inputs.Dispose(); _writeLengths.Dispose(); _writeBends.Dispose();
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
