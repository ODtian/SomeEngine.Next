using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render;
using Buffer = SomeEngine.Graphics.Buffer;

namespace DefaultGame;

internal sealed class DeviceCrowdMotorTargets : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly DevicePoseSources _sources;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintData<DeviceJointConstraint> _joints;
    private readonly DeviceConstraintData<DeviceMotorConstraint> _motors;
    private readonly Entity[] _targets;
    private readonly QueryHandle _rows, _references;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Buffer _inputs;
    private readonly BufferSrv _inputView;
    private readonly BufferUav _modelView, _bodyView;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private MappedBuffer _mapping;

    internal DeviceCrowdMotorTargets(IGraphicsBackend backend, Device device, World world,
        Entity[] targets, DevicePoseSources sources, DeviceBodies bodies,
        DeviceConstraintData<DeviceJointConstraint> joints, DeviceConstraintData<DeviceMotorConstraint> motors,
        ComputePipelineDesc kernel)
    {
        _backend = backend; _device = device; _world = world; _sources = sources; _bodies = bodies;
        _joints = joints; _motors = motors; _targets = targets;
        QueryDefinition rows = new QueryDefinitionBuilder().Read<CrowdMotorTarget>().Read<JointMotor>()
            .Read<JointMotorTarget>().Read<DeviceConstraintBinding<DeviceMotorConstraint>>().Build();
        DeviceMotorSource source = default;
        QueryDefinition references = source.AddReads(QueryDefinition.Empty.WithAccessFrom(rows)).WithOptionalRead<GpuPoseTarget>();
        _rows = world.Query(rows.WithAccess(references)); _references = world.Query(references);
        var resources = new QueryReadResources(); source.AddReadResources(resources);
        resources.Add<GpuPoseTarget, GpuPoseTargetResources>();
        resources.Add<DeviceConstraintBinding<DeviceMotorConstraint>, DeviceConstraintReferenceResources<DeviceMotorConstraint>>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(references)];
        QueryJobAccess.CopyAccesses(world, references, _queryAccesses, resources);
        _inputs = backend.CreateBuffer(device, new((ulong)motors.Capacity * 288, BufferUsages.ShaderRead,
            "Crowd motor component inputs"), MemoryType.Upload);
        _inputView = backend.CreateBufferSrv(device, new(_inputs, new(0, _inputs.Info.Size), StructureStride: 288));
        _mapping = backend.Map(_inputs, MapType.Write, BufferRange.Whole);
        _modelView = backend.CreateBufferUav(device, new(sources.Models, new(0, sources.Models.Info.Size), StructureStride: 64));
        _bodyView = backend.CreateBufferUav(device, new(bodies.States, new(0, bodies.States.Info.Size), StructureStride: DeviceBodies.StateStride));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Animation motor targets"));
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["CrowdMotorInputs"] = [ResourceBinding.ReadOnlyBuffer(_inputView)],
            ["CrowdMotorModels"] = [ResourceBinding.WritableBuffer(_modelView)],
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(_bodyView)],
        };
        joints.AddBindings(bindings); motors.AddBindings(bindings);
        _bindings = ShaderResourceLayout.Resolve<ResourceBinding>(_layout, (name, _) => bindings[name][0]);
    }

    internal WriteCrowdMotorTargets WriteTargets() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _sources.GetResultReadAccesses(scheduler),
            _sources.GetModelReadAccess(scheduler), _bodies.GetReadAccess(scheduler), .. _joints.GetReadAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_motors.Data), 0, (long)_motors.Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_motors.Metadata), 0, (long)_motors.Metadata.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_inputs), 0, (long)_inputs.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses, dependency);
    }

    private QueueCompletion Submit(in PhysicsStepContext context)
    {
        var read = new ReadInputs(this);
        _world.ExecuteQuery(_references, ref read, static (scoped QueryCursor references, ref ReadInputs read) =>
        {
            var write = new WriteInputs(read.Targets, references);
            read.Targets._world.ExecuteReadQuery(read.Targets._rows, ref write,
                static (scoped QueryCursor rows, ref WriteInputs write) => write.Execute(rows));
        });
        _mapping.Flush(_mapping.Range);
        var step = new MotorStep((uint)_motors.Capacity, context.Number < 0 ? 1u : 0u, (float)context.Duration, context.EndTime);
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_motors.Capacity + 63) / 64));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private readonly struct ReadInputs(DeviceCrowdMotorTargets targets)
    { internal readonly DeviceCrowdMotorTargets Targets = targets; }

    private readonly ref struct WriteInputs
    {
        private readonly DeviceCrowdMotorTargets _targets;
        private readonly QueryCursor _references;
        internal WriteInputs(DeviceCrowdMotorTargets targets, QueryCursor references)
        { _targets = targets; _references = references; }
        internal void Execute(scoped QueryCursor rows)
        {
            DeviceMotorSource source = default;
            foreach (QueryRow row in rows.Rows)
            {
                var binding = row.Read<DeviceConstraintBinding<DeviceMotorConstraint>>();
                if (!ReferenceEquals(binding.Data, _targets._motors)) continue;
                _references.TryGetRow(_targets._targets[binding.Index], out QueryRow target);
                var input = new MotorInput
                {
                    ModelToWorld = target.Read<GpuPoseTarget>().ModelToWorld,
                    Parameters = source.Read(in row.ReadRef<JointMotor>(), row, _references),
                    ReferencePosition = row.Read<CrowdMotorTarget>().Position,
                    ModelStart = (uint)_targets._sources.GetModelRange(target.Entity).Start,
                    SourceBone = 20, TargetBone = 22, MotorRow = (uint)binding.Index,
                    InputVersion = row.WriteVersion<JointMotor>(),
                };
                MemoryMarshal.Write(_targets._mapping.Bytes.Slice(binding.Index * 288, 288), in input);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 288)]
    private struct MotorInput
    {
        [FieldOffset(0)] public Matrix4x4 ModelToWorld;
        [FieldOffset(64)] public DeviceMotorConstraint Parameters;
        [FieldOffset(256)] public Vector3 ReferencePosition;
        [FieldOffset(268)] public uint ModelStart;
        [FieldOffset(272)] public uint SourceBone;
        [FieldOffset(276)] public uint TargetBone;
        [FieldOffset(280)] public uint MotorRow;
        [FieldOffset(284)] public uint InputVersion;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private readonly struct MotorStep(uint count, uint reset, float duration, double endTime)
    {
        [FieldOffset(0)] public readonly uint Count = count;
        [FieldOffset(4)] public readonly uint Reset = reset;
        [FieldOffset(8)] public readonly float Duration = duration;
        [FieldOffset(16)] public readonly double EndTime = endTime;
    }
    private readonly struct Submission(DeviceCrowdMotorTargets targets, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => targets._backend;
        public Device Device => targets._device;
        public QueueCompletion Submit() => targets.Submit(in context);
    }
    public void Dispose()
    {
        _mapping.Dispose(); _commands.Dispose(); _pipeline.Dispose(); _bodyView.Dispose(); _modelView.Dispose();
        _inputView.Dispose(); _inputs.Dispose(); _world.ReleaseQuery(_rows); _world.ReleaseQuery(_references);
    }
}

internal readonly struct WriteCrowdMotorTargets(DeviceCrowdMotorTargets targets) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => targets.Schedule(in context, dependency);
}
