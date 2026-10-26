using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceBodyKernels(ComputePipelineDesc Predict, ComputePipelineDesc UpdateVelocities,
    ComputePipelineDesc? GyroscopicTorque = null);

/// <summary>Integrates and reconstructs the selected entities in their existing device body buffer.</summary>
public sealed class DeviceBodyDynamics : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly QueryHandle _members;
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

    public DeviceBodyDynamics(IGraphicsBackend backend, Device device, World world, QueryHandle members,
        DeviceBodies bodies, DeviceBodyKernels kernels, int threadsPerGroup)
    {
        _backend = backend; _device = device; _world = world; _bodies = bodies; _threadsPerGroup = threadsPerGroup;
        QueryDefinition definition = world.GetQueryDefinition(members).AsReadOnly();
        _members = world.Query(definition);
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, resources);
        _indices = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4, BufferUsages.ShaderRead,
            "Selected device body indices"), MemoryType.Upload);
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Device body step"), MemoryType.Upload);
        _membership = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4, BufferUsages.ShaderRead,
            "Selected body membership"), MemoryType.Upload);
        _indicesView = backend.CreateBufferSrv(device, new(_indices, new(0, _indices.Info.Size), StructureStride: 4));
        _membershipView = backend.CreateBufferSrv(device, new(_membership, new(0, _membership.Info.Size), StructureStride: 4));
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body dynamics"));
        ComputePipelineDesc[] descriptions = kernels.GyroscopicTorque is { } gyro
            ? [kernels.Predict, kernels.UpdateVelocities, gyro] : [kernels.Predict, kernels.UpdateVelocities];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
        };
        AddSelectionBindings(bindings);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public PredictDeviceBodies Predict(Vector3 acceleration) => new(this, acceleration);
    public UpdateDeviceBodyVelocities UpdateVelocities() => new(this);
    public ApplyDeviceGyroscopicTorque ApplyGyroscopicTorque() => new(this);

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

    internal JobHandle Schedule(in PhysicsStepContext context, Vector3 acceleration, int kernel, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, _bodies.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_indices), 0, (long)_indices.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_membership), 0, (long)_membership.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_parameters), 0, 256),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Dynamics(this, context, acceleration, kernel), accesses, dependency);
    }

    private QueueCompletion Submit(in PhysicsStepContext context, Vector3 acceleration, int kernel)
    {
        int count = 0;
        using (MappedBuffer mapping = _backend.Map(_indices, MapType.Write, BufferRange.Whole))
        using (MappedBuffer membership = _backend.Map(_membership, MapType.Write, BufferRange.Whole))
        {
            membership.Bytes.Clear();
            var selection = new Selection(_bodies, mapping, membership);
            _world.ExecuteReadQuery(_members, ref selection,
                static (scoped QueryCursor rows, ref Selection selected) => selected.Read(rows));
            count = selection.Count;
            mapping.Flush(new(0, (ulong)count * 4));
            membership.Flush(membership.Range);
        }
        var parameters = new BodyStep((uint)count, (float)context.Duration, (uint)context.Substep,
            acceleration, context.Number, context.EndTime);
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, in parameters);
            mapping.Flush(new(0, (ulong)Marshal.SizeOf<BodyStep>()));
        }
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        if (count != 0) _backend.Dispatch(_commands, new((uint)((count + _threadsPerGroup - 1) / _threadsPerGroup), 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private struct Selection(DeviceBodies bodies, MappedBuffer mapping, MappedBuffer membership)
    {
        internal int Count;
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                DeviceBodyBinding binding = row.Read<DeviceBodyBinding>();
                if (ReferenceEquals(binding.Bodies, bodies))
                {
                    MemoryMarshal.Cast<byte, uint>(mapping.Bytes)[Count++] = (uint)binding.Index;
                    MemoryMarshal.Cast<byte, uint>(membership.Bytes)[binding.Index] = 1;
                }
            }
        }
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

    private readonly struct Dynamics(DeviceBodyDynamics dynamics, PhysicsStepContext context, Vector3 acceleration, int kernel)
        : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => dynamics._backend;
        public Device Device => dynamics._device;
        public QueueCompletion Submit() => dynamics.Submit(in context, acceleration, kernel);
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_members);
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _parametersView.Dispose(); _indicesView.Dispose(); _membershipView.Dispose();
        _parameters.Dispose(); _indices.Dispose(); _membership.Dispose();
    }
}

public readonly struct PredictDeviceBodies(DeviceBodyDynamics dynamics, Vector3 acceleration) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.Schedule(in context, acceleration, 0, dependency);
}
public readonly struct UpdateDeviceBodyVelocities(DeviceBodyDynamics dynamics) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.Schedule(in context, Vector3.Zero, 1, dependency);
}
public readonly struct ApplyDeviceGyroscopicTorque(DeviceBodyDynamics dynamics) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.Schedule(in context, Vector3.Zero, 2, dependency);
}
