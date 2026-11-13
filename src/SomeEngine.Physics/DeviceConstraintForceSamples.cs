using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Reads completed loads, with optional device filtering by force and torque limits.</summary>
public sealed class DeviceConstraintForceSamples<TData> : IDisposable where TData : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly Buffer _rows, _forces, _counter, _counterReadback, _zero;
    private Buffer _rowReadback, _forceReadback;
    private readonly BufferUav _rowView, _forceView, _counterView;
    private readonly BufferSrv _dummyRows, _dummyLimits;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private readonly JobResourceAccess[] _inputAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly DeviceConstraintForceLimits<TData>? _limits;
    private readonly int _threadsPerGroup;
    private readonly List<int> _reported = [];
    private bool _initialized;
    private int _resultCount;
    internal readonly DeviceConstraintData<TData> Parameters;
    internal readonly int BodiesPerConstraint;
    internal DeviceConstraintForceRow[] Rows = [];
    internal DeviceBodyForce[] Forces = [];
    public int Count => _resultCount;

    public DeviceConstraintForceSamples(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<TData> parameters, ComputePipelineDesc kernel, int bodiesPerConstraint,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections,
        DeviceConstraintForceLimits<TData>? limits = null)
    {
        _backend = backend; _device = device; _bodies = bodies; Parameters = parameters; _limits = limits;
        BodiesPerConstraint = bodiesPerConstraint; _threadsPerGroup = threadsPerGroup;
        _inputAccesses = inputAccesses.ToArray(); _selections = selections.ToArray();
        var rowDescription = new BufferDesc((ulong)parameters.Capacity * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        var forceDescription = new BufferDesc((ulong)parameters.Capacity * (ulong)bodiesPerConstraint * 48,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        _rows = parameters.Memory is { } memory ? memory.CreateBuffer(rowDescription, 16) : backend.CreateBuffer(device, rowDescription);
        _forces = parameters.Memory is { } contactMemory ? contactMemory.CreateBuffer(forceDescription, (uint)bodiesPerConstraint * 48)
            : backend.CreateBuffer(device, forceDescription);
        _counter = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination));
        _counterReadback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        _zero = backend.CreateBuffer(device, new(4, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer zero = backend.Map(_zero, MapType.Write, BufferRange.Whole)) { zero.Bytes.Clear(); zero.Flush(zero.Range); }
        _rowView = backend.CreateBufferUav(device, new(_rows, new(0, _rows.Info.Size), StructureStride: 16));
        _forceView = backend.CreateBufferUav(device, new(_forces, new(0, _forces.Info.Size), StructureStride: 48));
        _counterView = backend.CreateBufferUav(device, new(_counter, new(0, 4), StructureStride: 4));
        _dummyRows = backend.CreateBufferSrv(device, new(_rows, new(0, _rows.Info.Size), StructureStride: 4));
        _dummyLimits = backend.CreateBufferSrv(device, new(_forces, new(0, _forces.Info.Size), StructureStride: 8));
        _rowReadback = backend.CreateBuffer(device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        _forceReadback = backend.CreateBuffer(device, new((ulong)bodiesPerConstraint * 48, BufferUsages.CopyDestination), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Constraint force observations"));
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
            ["DeviceConstraintForceRows"] = [ResourceBinding.WritableBuffer(_rowView)],
            ["DeviceConstraintBodyForces"] = [ResourceBinding.WritableBuffer(_forceView)],
            ["DeviceConstraintForceCount"] = [ResourceBinding.WritableBuffer(_counterView)],
            ["DeviceConstraintForceInputRows"] = [limits?.RowsBinding ?? ResourceBinding.ReadOnlyBuffer(_dummyRows)],
            ["DeviceConstraintForceLimits"] = [limits?.LimitsBinding ?? ResourceBinding.ReadOnlyBuffer(_dummyLimits)],
        };
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
    }
    public ReadDeviceConstraintForces<TData> Readback() => new(this);
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(this))];
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputAccesses) { _bodies.GetReadAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        accesses.AddRange(Parameters.GetReadAccesses(scheduler));
        foreach (Buffer buffer in new[] { _rows, _forces, _counter, _counterReadback, _zero })
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        _limits?.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses.ToArray(), dependency);
        return scheduler.Schedule(new Map(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], copied);
    }
    private async ValueTask<QueueCompletion> RecordAsync(PhysicsStepContext context)
    {
        foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(in context);
        _limits?.Capture(in context);
        Parameters.Memory?.EnsureRows(1);
        int count = _limits?.Count ?? Parameters.RowCount;
        _resultCount = _limits is null ? count : 0;
        if (_limits is null) GrowReadback(count);
        var step = new DeviceConstraintForceStep((float)context.Duration, (uint)count,
            (uint)BodiesPerConstraint, (uint)context.Substep, context.Number, context.StartTime, context.EndTime, _limits is null ? 0UL : 1UL);
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized)
            foreach (Buffer buffer in new[] { _rows, _forces, _counter })
                if (Parameters.Memory?.Contains(buffer) != true) _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(_counter, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(_zero, 0, _counter, 0, 4));
        _backend.Barrier(_commands, new BufferBarrier(_counter, PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        if (count != 0) _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((count + _threadsPerGroup - 1) / _threadsPerGroup));
        if (_limits is null) CopyResults(); else Copy(_counter, _counterReadback, 4);
        QueueCompletion completion;
        using (RecordedCommands recorded = _backend.End(_commands))
            completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        if (_limits is null) return completion;
        await _backend.WaitAsync(completion).ConfigureAwait(false);
        using (MappedBuffer mapping = _backend.Map(_counterReadback, MapType.Read, new(0, 4)))
        { mapping.Invalidate(mapping.Range); _resultCount = MemoryMarshal.Read<int>(mapping.Bytes); }
        if (_resultCount == 0) return completion;
        GrowReadback(_resultCount);
        _backend.Begin(_commands, new(0, 0, 8)); CopyResults();
        using RecordedCommands prefix = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [prefix], [], []));
    }
    private void GrowReadback(int count)
    {
        if (_rowReadback.Info.Size < (ulong)count * 16)
        { _rowReadback.Dispose(); _rowReadback = _backend.CreateBuffer(_device, new((ulong)count * 16, BufferUsages.CopyDestination), MemoryType.Readback); }
        if (_forceReadback.Info.Size < (ulong)count * (ulong)BodiesPerConstraint * 48)
        { _forceReadback.Dispose(); _forceReadback = _backend.CreateBuffer(_device, new((ulong)count * (ulong)BodiesPerConstraint * 48, BufferUsages.CopyDestination), MemoryType.Readback); }
    }
    private void CopyResults()
    {
        if (_resultCount == 0) return;
        Copy(_rows, _rowReadback, (ulong)_resultCount * 16);
        Copy(_forces, _forceReadback, (ulong)_resultCount * (ulong)BodiesPerConstraint * 48);
    }
    private void Copy(Buffer source, Buffer destination, ulong bytes)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(source, 0, destination, 0, bytes));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }
    private readonly struct Submission(DeviceConstraintForceSamples<TData> samples, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => samples._backend;
        public Device Device => samples._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => samples.RecordAsync(context).AsTask().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => samples.RecordAsync(context);
    }
    private readonly struct Map(DeviceConstraintForceSamples<TData> samples) : IJob
    {
        public void Execute()
        {
            foreach (int row in samples._reported) samples.Rows[row] = default;
            samples._reported.Clear();
            if (samples.Rows.Length < samples.Parameters.RowCount) Array.Resize(ref samples.Rows, samples.Parameters.RowCount);
            if (samples.Forces.Length < samples.Parameters.RowCount * samples.BodiesPerConstraint) Array.Resize(ref samples.Forces, samples.Parameters.RowCount * samples.BodiesPerConstraint);
            if (samples._resultCount == 0) return;
            using MappedBuffer rows = samples._backend.Map(samples._rowReadback, MapType.Read, new(0, (ulong)samples._resultCount * 16));
            using MappedBuffer forces = samples._backend.Map(samples._forceReadback, MapType.Read, new(0, (ulong)samples._resultCount * (ulong)samples.BodiesPerConstraint * 48));
            rows.Invalidate(rows.Range); forces.Invalidate(forces.Range);
            ReadOnlySpan<DeviceConstraintForceRow> values = MemoryMarshal.Cast<byte, DeviceConstraintForceRow>(rows.Bytes);
            ReadOnlySpan<DeviceBodyForce> bodyForces = MemoryMarshal.Cast<byte, DeviceBodyForce>(forces.Bytes);
            for (int index = 0; index < values.Length; index++)
            {
                DeviceConstraintForceRow value = values[index];
                if (value.Active == 0) continue;
                int row = (int)value.Active - 1;
                samples.Rows[row] = value; samples._reported.Add(row);
                bodyForces.Slice(index * samples.BodiesPerConstraint, (int)value.BodyCount).CopyTo(samples.Forces.AsSpan(row * samples.BodiesPerConstraint));
            }
            samples._backend.CollectCompleted(samples._device);
        }
    }
    public void Dispose()
    {
        _pipeline.Dispose(); _commands.Dispose(); _rowView.Dispose(); _forceView.Dispose(); _counterView.Dispose(); _dummyRows.Dispose(); _dummyLimits.Dispose();
        foreach (Buffer buffer in new[] { _rows, _forces, _counter, _counterReadback, _zero, _rowReadback, _forceReadback }) buffer.Dispose();
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
        resources.Read(component.Samples);
    }
}

/// <summary>Reads completed device loads through the original per-body force interface.</summary>
public readonly struct DeviceConstraintForces<TConstraint, TData> : IPhysicsConstraintForces<TConstraint>
    where TConstraint : struct, IComponent where TData : unmanaged
{
    public static void AddForceInputAccesses(World world, Query query, List<JobResourceAccess> accesses) =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess<ForceRead>(query
            .WithOptionalRead<DeviceConstraintForceBinding<TData>>().WithOptionalRead<DeviceConstraintBinding<TData>>()));
    public static void AddForceAccesses(World world, Query query, List<JobResourceAccess> accesses)
    {
        Query source = query.WithOptionalRead<DeviceConstraintForceBinding<TData>>().WithOptionalRead<DeviceConstraintBinding<TData>>();
        var jobs = (WorldJobs)world.Execution!;
        accesses.AddRange(jobs.CollectAccess<ForceRead>(source));
        accesses.AddRange(jobs.CollectResources(source, new ForceRead()));
    }
    private readonly struct ForceRead : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.ReadOptional<DeviceConstraintForceBinding<TData>>(true),
            ChunkAccess.ReadOptional<DeviceConstraintBinding<TData>>(true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            if (!rows.TryRead<DeviceConstraintForceBinding<TData>>(out var bindings)) return;
            for (int row = 0; row < rows.Count; row++)
                if (rows.Has<DeviceConstraintForceBinding<TData>>(row)) resources.Read(bindings[row].Samples);
        }
        public void Execute(SystemChunk rows) { }
    }
    public static int GetBodyCount(in TConstraint constraint, World world, Entity entity, in PhysicsStepContext context) => ReadRow(world, entity, out _, out _);
    public static Entity GetBody(in TConstraint constraint, World world, Entity entity, int index, in PhysicsStepContext context)
    {
        var samples = world.ReadComponentData<DeviceConstraintForceBinding<TData>>(entity).Samples;
        int start = world.ReadComponentData<DeviceConstraintBinding<TData>>(entity).Index * samples.BodiesPerConstraint;
        return samples.Forces[start + index].Body;
    }
    public static void GetBodyForce(in TConstraint constraint, World world, Entity entity, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        int count = ReadRow(world, entity, out DeviceConstraintForceSamples<TData> samples, out int start);
        force = torque = Vector3.Zero;
        for (int index = 0; index < count; index++)
            if (samples.Forces[start + index].Body == body)
            { force = samples.Forces[start + index].Force; torque = samples.Forces[start + index].Torque; return; }
    }
    private static int ReadRow(World world, Entity entity, out DeviceConstraintForceSamples<TData> samples, out int start)
    {
        samples = world.ReadComponentData<DeviceConstraintForceBinding<TData>>(entity).Samples;
        var binding = world.ReadComponentData<DeviceConstraintBinding<TData>>(entity);
        start = binding.Index * samples.BodiesPerConstraint;
        if (!ReferenceEquals(binding.Data, samples.Parameters) || binding.Index >= samples.Rows.Length) return 0;
        ref readonly DeviceConstraintForceRow value = ref samples.Rows[binding.Index];
        return value.Active != 0 && value.EntityIndex == (uint)entity.Id && value.EntityGeneration == (uint)entity.Generation ? (int)value.BodyCount : 0;
    }
}
