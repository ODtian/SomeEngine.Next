using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceBodyKernels(ComputePipelineDesc Predict, ComputePipelineDesc UpdateVelocities,
    ComputePipelineDesc? GyroscopicTorque = null, ComputePipelineDesc? Resume = null);

/// <summary>Integrates and reconstructs the selected entities in their existing device body buffer.</summary>
public sealed class BodyDynamicsCommands : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly Query _members;
    private readonly DeviceBodies _bodies;
    private readonly int _threadsPerGroup;
    private readonly Buffer _indices, _membership, _parameters;
    private readonly BufferSrv _indicesView;
    private readonly BufferSrv _membershipView;
    private readonly BufferCbv _parametersView;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly int _gyroKernel, _resumeKernel;
    private ulong _membershipVersion;
    private readonly PhysicsQueryWindow _selectionWindow;
    private int _selectionCount;
    private bool _hasSelection;

    public BodyDynamicsCommands(IGraphicsBackend backend, Device device, World world, Query members,
        DeviceBodies bodies, DeviceBodyKernels kernels, int threadsPerGroup,
        IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null, ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend; _device = device; _world = world; _bodies = bodies; _threadsPerGroup = threadsPerGroup;
        _extraAccesses = extraAccesses.ToArray();
        _members = SystemRunner.Bind<BindingReads>(members);
        _selectionWindow = new(_members);
        _queryAccesses = ((WorldJobs)world.Execution!).CollectReadAccess<DeviceBodyBinding, DeviceBodyReferenceResources>(_members);
        _membershipVersion = _members.MembershipVersion;
        _indices = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4, BufferUsages.ShaderRead,
            "Selected device body indices"), MemoryType.Upload);
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Device body step"), MemoryType.Upload);
        _membership = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4, BufferUsages.ShaderRead,
            "Selected body membership"), MemoryType.Upload);
        _indicesView = backend.CreateBufferSrv(device, new(_indices, new(0, _indices.Info.Size), StructureStride: 4));
        _membershipView = backend.CreateBufferSrv(device, new(_membership, new(0, _membership.Info.Size), StructureStride: 4));
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body dynamics"));
        var descriptions = new List<ComputePipelineDesc> { kernels.Predict, kernels.UpdateVelocities };
        _gyroKernel = _resumeKernel = -1;
        if (kernels.GyroscopicTorque is { } gyro) { _gyroKernel = descriptions.Count; descriptions.Add(gyro); }
        if (kernels.Resume is { } resume) { _resumeKernel = descriptions.Count; descriptions.Add(resume); }
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
        };
        AddSelectionBindings(bindings);
        if (extraBindings is not null)
            foreach (var binding in extraBindings) bindings.Add(binding.Key, binding.Value);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public Commands.PredictBodies Predict(Vector3 acceleration) => new(this, acceleration);
    public Commands.UpdateBodyVelocities UpdateVelocities() => new(this);
    public Commands.ApplyGyroscopicTorque ApplyGyroscopicTorque() => new(this);
    public Commands.ResumeBodies Resume(Vector3 acceleration) => new(this, acceleration);
    public Commands.UpdateBodySelection UpdateSelection() => new(this);
    internal JobHandle ScheduleResume(in PhysicsStepContext context, Vector3 acceleration, JobHandle dependency) =>
        Schedule(in context, acceleration, _resumeKernel, dependency);
    internal JobHandle ScheduleGyroscopicTorque(in PhysicsStepContext context, JobHandle dependency) =>
        Schedule(in context, Vector3.Zero, _gyroKernel, dependency);

    internal void AddSelectionBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("BodyStep", [ResourceBinding.ConstantBuffer(_parametersView)]);
        bindings.Add("DeviceBodyIndices", [ResourceBinding.ReadOnlyBuffer(_indicesView)]);
        bindings.Add("DeviceBodyMembership", [ResourceBinding.ReadOnlyBuffer(_membershipView)]);
    }
    internal JobResourceAccess[] GetSelectionReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256),
            JobResourceAccess.Read(scheduler.GetResourceToken(_membership), 0, (long)_membership.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_indices), 0, (long)_indices.Info.Size)];

    private JobResourceAccess[] GetSelectionWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(_parameters), 0, 256),
            JobResourceAccess.Write(scheduler.GetResourceToken(_membership), 0, (long)_membership.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_indices), 0, (long)_indices.Info.Size)];

    internal JobHandle ScheduleSelection(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        return scheduler.Schedule(new ReadSelection(this, context),
            [.. _queryAccesses, .. GetSelectionWriteAccesses(scheduler)], dependency);
    }

    internal JobHandle Schedule(in PhysicsStepContext context, Vector3 acceleration, int kernel, JobHandle dependency)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _extraAccesses, _bodies.GetWriteAccess(scheduler),
            .. GetSelectionWriteAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Dynamics(this, context, acceleration, kernel), accesses, dependency);
    }

    private int CaptureSelection(in PhysicsStepContext context, Vector3 acceleration)
    {
        ulong baseline = _selectionWindow.Begin(context);
        ulong membershipVersion = _members.MembershipVersion;
        bool changed = !_hasSelection || membershipVersion != _membershipVersion || _selectionWindow.UsesVersions;
        using QueryScope rows = _members.Since(baseline).OpenRead();
        ulong observed = _world.Version;
        if (!changed)
            foreach (QueryChunk chunk in rows)
                if (chunk.WasWritten<DeviceBodyBinding>(baseline)) { changed = true; break; }
        if (changed)
        {
            using MappedBuffer mapping = _backend.Map(_indices, MapType.Write, BufferRange.Whole);
            using MappedBuffer membership = _backend.Map(_membership, MapType.Write, BufferRange.Whole);
            Span<uint> indices = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            Span<uint> flags = MemoryMarshal.Cast<byte, uint>(membership.Bytes);
            flags.Clear(); _selectionCount = 0;
            foreach (QueryChunk chunk in rows)
                foreach (ref readonly DeviceBodyBinding binding in chunk.Read<DeviceBodyBinding>())
                    if (ReferenceEquals(binding.Bodies, _bodies))
                    {
                        indices[_selectionCount++] = (uint)binding.Index;
                        flags[binding.Index] = 1;
                    }
            if (_selectionCount != 0) mapping.Flush(new(0, (ulong)_selectionCount * 4));
            membership.Flush(membership.Range); _hasSelection = true;
        }
        _membershipVersion = membershipVersion; _selectionWindow.Accept(observed);
        var parameters = new BodyStep((uint)_selectionCount, (float)context.Duration, (uint)context.Substep,
            acceleration, context.Number, context.EndTime);
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, in parameters);
            mapping.Flush(new(0, (ulong)Marshal.SizeOf<BodyStep>()));
        }
        return _selectionCount;
    }
    private QueueCompletion Submit(in PhysicsStepContext context, Vector3 acceleration, int kernel)
    {
        int count = CaptureSelection(in context, acceleration);
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        if (count != 0) _backend.Dispatch(_commands, new((uint)((count + _threadsPerGroup - 1) / _threadsPerGroup), 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private readonly struct BindingReads : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows) { }
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct BodyStep
    {
        [FieldOffset(0)] public uint Count;
        [FieldOffset(4)] public float Duration;
        [FieldOffset(8)] public uint Substep;
        [FieldOffset(16)] public Vector3 Acceleration;
        [FieldOffset(32)] public long Number;
        [FieldOffset(40)] public double Time;
        internal BodyStep(uint count, float duration, uint substep, Vector3 acceleration, long number, double time) =>
            (Count, Duration, Substep, Acceleration, Number, Time) = (count, duration, substep, acceleration, number, time);
    }

    private readonly struct Dynamics(BodyDynamicsCommands dynamics, PhysicsStepContext context, Vector3 acceleration, int kernel)
        : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => dynamics._backend;
        public Device Device => dynamics._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => dynamics.Submit(in context, acceleration, kernel);
    }
    private readonly struct ReadSelection(BodyDynamicsCommands dynamics, PhysicsStepContext context) : IJob
    {
        public void Execute() => dynamics.CaptureSelection(in context, Vector3.Zero);
    }

    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _parametersView.Dispose(); _indicesView.Dispose(); _membershipView.Dispose();
        _parameters.Dispose(); _indices.Dispose(); _membership.Dispose();
    }
}
