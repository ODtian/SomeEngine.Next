using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Completed world forces and center-of-mass torques from a concrete device constraint type.</summary>
public sealed class DeviceConstraintForceSamples<TData> : IDisposable where TData : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly Buffer _rows, _forces, _rowReadback, _forceReadback;
    private readonly BufferUav _rowView, _forceView;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private readonly JobResourceAccess[] _inputAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly int _threadsPerGroup;
    private bool _initialized;
    internal readonly DeviceConstraintData<TData> Parameters;
    internal readonly int BodiesPerConstraint;
    internal readonly DeviceConstraintForceRow[] Rows;
    internal readonly DeviceBodyForce[] Forces;

    public DeviceConstraintForceSamples(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<TData> parameters, ComputePipelineDesc kernel, int bodiesPerConstraint,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections)
    {
        _backend = backend; _device = device; _bodies = bodies; Parameters = parameters;
        BodiesPerConstraint = bodiesPerConstraint; _threadsPerGroup = threadsPerGroup;
        _inputAccesses = inputAccesses.ToArray(); _selections = selections.ToArray();
        Rows = new DeviceConstraintForceRow[parameters.Capacity];
        Forces = new DeviceBodyForce[parameters.Capacity * bodiesPerConstraint];
        _rows = backend.CreateBuffer(device, new((ulong)Rows.Length * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Constraint force identities"), MemoryType.DeviceLocal);
        _forces = backend.CreateBuffer(device, new((ulong)Forces.Length * 48,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Current constraint body forces"), MemoryType.DeviceLocal);
        _rowView = backend.CreateBufferUav(device, new(_rows, new(0, _rows.Info.Size), StructureStride: 16));
        _forceView = backend.CreateBufferUav(device, new(_forces, new(0, _forces.Info.Size), StructureStride: 48));
        _rowReadback = backend.CreateBuffer(device, new(_rows.Info.Size, BufferUsages.CopyDestination,
            "Completed constraint force identities"), MemoryType.Readback);
        _forceReadback = backend.CreateBuffer(device, new(_forces.Info.Size, BufferUsages.CopyDestination,
            "Completed constraint body forces"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Constraint force observations"));
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
            ["DeviceConstraintForceRows"] = [ResourceBinding.WritableBuffer(_rowView)],
            ["DeviceConstraintBodyForces"] = [ResourceBinding.WritableBuffer(_forceView)],
        };
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
    }

    public ReadDeviceConstraintForces<TData> Readback() => new(this);
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler, Rows), JobResourceAccess.Read(scheduler, Forces)];

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputAccesses) { _bodies.GetReadAccess(scheduler) };
        accesses.AddRange(Parameters.GetReadAccesses(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_rows), 0, (long)_rows.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_forces), 0, (long)_forces.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_rowReadback), 0, (long)_rowReadback.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_forceReadback), 0, (long)_forceReadback.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses.ToArray(), dependency);
        return scheduler.Schedule(new Map(this), new[]
        {
            JobResourceAccess.Read(scheduler.GetResourceToken(_rowReadback), 0, (long)_rowReadback.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_forceReadback), 0, (long)_forceReadback.Info.Size),
            JobResourceAccess.Write(scheduler, Rows), JobResourceAccess.Write(scheduler, Forces),
        }, copied);
    }
    private QueueCompletion Record(in PhysicsStepContext context)
    {
        foreach (IDeviceConstraintSelection selection in _selections) selection.Capture();
        var step = new DeviceConstraintForceStep((float)context.Duration, (uint)Parameters.Capacity,
            (uint)BodiesPerConstraint, (uint)context.Substep, context.Number, context.StartTime, context.EndTime, 0);
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized)
        {
            _backend.Barrier(_commands, new BufferBarrier(_rows, _rows.InitialSync, PipelineSync.ComputeShading,
                _rows.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_forces, _forces.InitialSync, PipelineSync.ComputeShading,
                _forces.InitialAccess, ResourceAccess.UnorderedAccess));
        }
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((Parameters.Capacity + _threadsPerGroup - 1) / _threadsPerGroup));
        Copy(_rows, _rowReadback); Copy(_forces, _forceReadback);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Copy(Buffer source, Buffer destination)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(source, 0, destination, 0, source.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }
    private readonly struct Submission(DeviceConstraintForceSamples<TData> samples, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => samples._backend;
        public Device Device => samples._device;
        public QueueCompletion Submit() => samples.Record(in context);
    }
    private readonly struct Map(DeviceConstraintForceSamples<TData> samples) : IJob
    {
        public void Execute()
        {
            using (MappedBuffer mapping = samples._backend.Map(samples._rowReadback, MapType.Read, BufferRange.Whole))
            {
                mapping.Invalidate(mapping.Range);
                MemoryMarshal.Cast<byte, DeviceConstraintForceRow>(mapping.Bytes).CopyTo(samples.Rows);
            }
            using (MappedBuffer mapping = samples._backend.Map(samples._forceReadback, MapType.Read, BufferRange.Whole))
            {
                mapping.Invalidate(mapping.Range);
                MemoryMarshal.Cast<byte, DeviceBodyForce>(mapping.Bytes).CopyTo(samples.Forces);
            }
            samples._backend.CollectCompleted(samples._device);
        }
    }
    public void Dispose()
    {
        _pipeline.Dispose(); _commands.Dispose(); _rowView.Dispose(); _forceView.Dispose();
        _rows.Dispose(); _forces.Dispose(); _rowReadback.Dispose(); _forceReadback.Dispose();
    }
}

public readonly struct ReadDeviceConstraintForces<TData>(DeviceConstraintForceSamples<TData> samples) : IPhysicsStage where TData : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => samples.Schedule(in context, dependency);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct DeviceConstraintForceStep(float Duration, uint Rows, uint BodiesPerConstraint,
    uint Substep, long Number, double StartTime, double EndTime, ulong Padding);

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct DeviceConstraintForceRow
{
    [FieldOffset(0)] public uint EntityIndex;
    [FieldOffset(4)] public uint EntityGeneration;
    [FieldOffset(8)] public uint BodyCount;
    [FieldOffset(12)] public uint Active;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DeviceBodyForce
{
    [FieldOffset(0)] public Entity Body;
    [FieldOffset(16)] public Vector3 Force;
    [FieldOffset(32)] public Vector3 Torque;
}

/// <summary>Selects the completed force observations belonging to the original parameter row.</summary>
public readonly record struct DeviceConstraintForceBinding<TData>(DeviceConstraintForceSamples<TData> Samples) : IComponent where TData : unmanaged;

public readonly struct DeviceConstraintForceResources<TData> : IJobComponentResources<DeviceConstraintForceBinding<TData>> where TData : unmanaged
{
    public static void Collect(in DeviceConstraintForceBinding<TData> component, ref JobResourceAccessBuilder resources)
    {
        resources.Read(component.Samples.Rows.AsMemory()); resources.Read(component.Samples.Forces.AsMemory());
    }
}

/// <summary>Reads completed device loads through the original per-body force interface.</summary>
public readonly struct DeviceConstraintForces<TConstraint, TData> : IPhysicsConstraintForces<TConstraint>
    where TConstraint : struct, IComponent where TData : unmanaged
{
    public static int GetBodyCount(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        ReadRow(row, out _, out _);

    public static Entity GetBody(in TConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        DeviceConstraintForceSamples<TData> samples = row.ReadRef<DeviceConstraintForceBinding<TData>>().Samples;
        int start = row.ReadRef<DeviceConstraintBinding<TData>>().Index * samples.BodiesPerConstraint;
        return samples.Forces[start + index].Body;
    }

    public static void GetBodyForce(in TConstraint constraint, scoped QueryRow row, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        int count = ReadRow(row, out DeviceConstraintForceSamples<TData> samples, out int start);
        force = torque = Vector3.Zero;
        for (int index = 0; index < count; index++)
            if (samples.Forces[start + index].Body == body)
            {
                force = samples.Forces[start + index].Force; torque = samples.Forces[start + index].Torque;
                return;
            }
    }

    private static int ReadRow(scoped QueryRow row, out DeviceConstraintForceSamples<TData> samples, out int start)
    {
        samples = row.ReadRef<DeviceConstraintForceBinding<TData>>().Samples;
        ref readonly var binding = ref row.ReadRef<DeviceConstraintBinding<TData>>();
        start = binding.Index * samples.BodiesPerConstraint;
        if (!ReferenceEquals(binding.Data, samples.Parameters)) return 0;
        ref readonly DeviceConstraintForceRow value = ref samples.Rows[binding.Index];
        return value.Active != 0 && value.EntityIndex == (uint)row.Entity.Index && value.EntityGeneration == (uint)row.Entity.Generation
            ? (int)value.BodyCount : 0;
    }
}
