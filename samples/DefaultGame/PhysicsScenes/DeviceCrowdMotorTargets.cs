using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
    private readonly Query _rows;
    private readonly SystemRunner _runner;
    private readonly ulong[] _motorEpochs;
    private readonly uint[] _revisions;
    private ulong _captureVersion;
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
        _rows = world.Query(new QueryDescription().With<CrowdMotorTarget>().With<JointMotor>()
            .With<JointMotorTarget>().With<DeviceConstraintBinding<DeviceMotorConstraint>>()
            .With<DeviceConstraintBinding<DeviceJointConstraint>>());
        _runner = new(world, 256); _motorEpochs = new ulong[motors.Capacity]; _revisions = new uint[motors.Capacity];
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
        Scheduler scheduler = _world.Execution!.Scheduler!;
        JobHandle written = _runner.Schedule(_rows, new WriteInputs(this), dependency, parallel: false);
        JobResourceAccess[] accesses = [.. _sources.GetResultReadAccesses(scheduler),
            _sources.GetModelReadAccess(scheduler), _bodies.GetReadAccess(scheduler), .. _joints.GetReadAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_motors.Data), 0, (long)_motors.Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_motors.Metadata), 0, (long)_motors.Metadata.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_inputs), 0, (long)_inputs.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses, written);
    }

    private QueueCompletion Submit(in PhysicsStepContext context)
    {
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

    private readonly struct WriteInputs(DeviceCrowdMotorTargets targets) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<CrowdMotorTarget>(), ChunkAccess.Read<JointMotor>(), ChunkAccess.Read<JointMotorTarget>(),
                ChunkAccess.Read<DeviceConstraintBinding<DeviceMotorConstraint>>(hasResources: true),
                ChunkAccess.Read<DeviceConstraintBinding<DeviceJointConstraint>>(hasResources: true),
                ChunkAccess.ReadOptional<GpuPoseTarget>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public static SystemAccesses AdditionalAccesses => new SystemAccesses().ReadRandom<GpuPoseTarget>();
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(targets); resources.Read(targets._sources);
            resources.Write(targets._inputs, 0, (long)targets._inputs.Info.Size);
            targets._captureVersion = targets._world.Version;
        }
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<DeviceConstraintBinding<DeviceMotorConstraint>> bindings = rows.Read<DeviceConstraintBinding<DeviceMotorConstraint>>();
            ReadOnlySpan<CrowdMotorTarget> goals = rows.Read<CrowdMotorTarget>();
            Span<DeviceMotorConstraint> parameters = stackalloc DeviceMotorConstraint[rows.Count];
            new DeviceMotorSource().Write(rows, targets._world, 0, default, parameters);
            Span<MotorInput> inputs = MemoryMarshal.Cast<byte, MotorInput>(targets._mapping.Bytes);
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceConstraintBinding<DeviceMotorConstraint> binding = bindings[row];
                if (!ReferenceEquals(binding.Data, targets._motors)) continue;
                int index = binding.Index;
                Entity target = targets._targets[index];
                if (rows.WasWritten<JointMotor>(row, targets._motorEpochs[index]))
                {
                    targets._revisions[index]++; targets._motorEpochs[index] = targets._captureVersion;
                }
                inputs[index] = new MotorInput
                {
                    ModelToWorld = targets._world.Read<GpuPoseTarget>(target).ModelToWorld,
                    Parameters = parameters[row], ReferencePosition = goals[row].Position,
                    ModelStart = (uint)targets._sources.GetModelRange(target).Start,
                    SourceBone = 20, TargetBone = 22, MotorRow = (uint)index, InputVersion = targets._revisions[index],
                };
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
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => targets.Submit(in context);
    }
    public void Dispose()
    {
        _mapping.Dispose(); _commands.Dispose(); _pipeline.Dispose(); _bodyView.Dispose(); _modelView.Dispose();
        _inputView.Dispose(); _inputs.Dispose();
    }
}

internal readonly struct WriteCrowdMotorTargets(DeviceCrowdMotorTargets targets) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => targets.Schedule(in context, dependency);
}
