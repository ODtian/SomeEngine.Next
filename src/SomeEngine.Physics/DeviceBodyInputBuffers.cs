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

public readonly record struct DeviceBodyInputKernels(ComputePipelineDesc Apply, ComputePipelineDesc KinematicTargets);

[Flags]
public enum DeviceBodyInputKinds : uint { Momentum = 1, Velocity = 2, Kinematic = 4 }

/// <summary>The selected step's physical inputs, derived from original timed component buffers.</summary>
[StructLayout(LayoutKind.Explicit, Size = 112)]
public struct DeviceBodyInput
{
    [FieldOffset(0)] public uint BodyIndex;
    [FieldOffset(4)] public uint EntityIndex;
    [FieldOffset(8)] public uint EntityGeneration;
    [FieldOffset(12)] public DeviceBodyInputKinds Kinds;
    [FieldOffset(16)] public Vector3 LinearImpulse;
    [FieldOffset(32)] public Vector3 AngularImpulse;
    [FieldOffset(48)] public Vector3 TargetLinearVelocity;
    [FieldOffset(64)] public Vector3 TargetAngularVelocity;
    [FieldOffset(80)] public Vector3 TargetPosition;
    [FieldOffset(96)] public Quaternion TargetRotation;
}

/// <summary>Reads timed body inputs once, then applies them to current resident mass, pose and velocity.</summary>
public sealed class DeviceBodyInputBuffers : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly QueryHandle _members;
    private readonly DeviceBodies _bodies;
    private readonly JobResourceAccess[] _queryAccesses, _extraAccesses;
    private readonly Buffer _inputs, _parameters;
    private readonly BufferSrv _inputsView;
    private readonly BufferCbv _parametersView;
    private MappedBuffer _inputMapping, _parameterMapping;
    private readonly int[] _indices;
    private int _count;
    private readonly int _threadsPerGroup;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;

    public DeviceBodyInputBuffers(IGraphicsBackend backend, Device device, World world, QueryHandle members,
        DeviceBodies bodies, DeviceBodyInputKernels kernels, int threadsPerGroup,
        IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null, ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend; _device = device; _world = world; _bodies = bodies; _threadsPerGroup = threadsPerGroup;
        _extraAccesses = extraAccesses.ToArray(); _indices = new int[bodies.Capacity];
        QueryDefinition definition = world.GetQueryDefinition(members).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<DeviceBodyBinding>().OptionalBuffer<BodyForce>(QueryAccess.Read).OptionalBuffer<BodyImpulse>(QueryAccess.Read)
            .OptionalBuffer<BodyVelocityTarget>(QueryAccess.Read).OptionalBuffer<BodyKinematicTarget>(QueryAccess.Read).Build());
        _members = world.Query(definition);
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, resources);
        _inputs = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 112, BufferUsages.ShaderRead,
            "Timed body inputs"), MemoryType.Upload);
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Timed body input count"), MemoryType.Upload);
        _inputsView = backend.CreateBufferSrv(device, new(_inputs, new(0, _inputs.Info.Size), StructureStride: 112));
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _inputMapping = backend.Map(_inputs, MapType.Write, BufferRange.Whole);
        _parameterMapping = backend.Map(_parameters, MapType.Write, BufferRange.Whole);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Apply timed body inputs"));
        ComputePipelineDesc[] descriptions = [kernels.Apply, kernels.KinematicTargets];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]> { ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)] };
        AddBindings(bindings);
        if (extraBindings is not null) foreach (var binding in extraBindings) bindings.Add(binding.Key, binding.Value);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public WriteDeviceBodyInputBuffers WriteInputs() => new(this);
    public ApplyDeviceBodyInputBuffers Apply() => new(this, 0);
    public ApplyDeviceBodyInputBuffers ApplyKinematicTargets() => new(this, 1);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceBodyInputs", [ResourceBinding.ReadOnlyBuffer(_inputsView)]);
        bindings.Add("BodyInputParameters", [ResourceBinding.ConstantBuffer(_parametersView)]);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(this)),
            JobResourceAccess.Read(scheduler.GetResourceToken(_inputs), 0, (long)_inputs.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256)];

    internal JobHandle ScheduleWrite(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_inputs), 0, (long)_inputs.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_parameters), 0, 256)];
        return scheduler.Schedule(new ReadInputs(this, context.StartTime, context.EndTime), accesses, dependency);
    }
    private readonly struct ReadInputs(DeviceBodyInputBuffers inputs, double start, double end) : IJob
    {
        public void Execute()
        {
            inputs._count = 0;
            var read = new ReadRows(inputs, start, end);
            inputs._world.ExecuteReadQuery(inputs._members, ref read,
                static (scoped QueryCursor rows, ref ReadRows read) => read.Execute(rows));
            Array.Sort(inputs._indices, 0, inputs._count);
            if (inputs._count != 0) inputs._inputMapping.Flush(new(0, (ulong)inputs._count * 112));
            var parameters = new InputParameters((uint)inputs._count);
            MemoryMarshal.Write(inputs._parameterMapping.Bytes, in parameters);
            inputs._parameterMapping.Flush(new(0, 16));
        }
    }
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct InputParameters(uint Count);

    internal JobHandle ScheduleApply(in PhysicsStepContext context, int kernel, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        return scheduler.Schedule(new ApplyInputs(this, scheduler, kernel), GetReadAccesses(scheduler), dependency);
    }
    private readonly struct ApplyInputs(DeviceBodyInputBuffers inputs, Scheduler scheduler, int kernel) : IJob
    {
        public void Execute()
        {
            var accesses = new List<JobResourceAccess>(inputs.GetReadAccesses(scheduler));
            accesses.AddRange(inputs._extraAccesses);
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(inputs._commands)));
            int first = 0;
            while (first < inputs._count)
            {
                int last = first;
                while (last + 1 < inputs._count && inputs._indices[last + 1] == inputs._indices[last] + 1) last++;
                accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(inputs._bodies.States),
                    (long)inputs._indices[first] * DeviceBodies.StateStride, (last - first + 1L) * DeviceBodies.StateStride));
                first = last + 1;
            }
            DevicePhysicsJobs.Schedule(scheduler, new SubmitInputs(inputs, kernel), accesses.ToArray(), default);
        }
    }
    private QueueCompletion Submit(int kernel)
    {
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        if (_count != 0) _backend.Dispatch(_commands, new((uint)((_count + _threadsPerGroup - 1) / _threadsPerGroup), 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct SubmitInputs(DeviceBodyInputBuffers inputs, int kernel) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._backend;
        public Device Device => inputs._device;
        public QueueCompletion Submit() => inputs.Submit(kernel);
    }
    public void Dispose()
    {
        _world.ReleaseQuery(_members);
        _inputMapping.Dispose(); _parameterMapping.Dispose();
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _inputsView.Dispose(); _parametersView.Dispose(); _inputs.Dispose(); _parameters.Dispose();
    }

    private readonly record struct ReadRows(DeviceBodyInputBuffers Inputs, double Start, double End)
    {
        internal void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                var binding = row.Read<DeviceBodyBinding>();
                if (!ReferenceEquals(binding.Bodies, Inputs._bodies)) continue;
                var input = new DeviceBodyInput { BodyIndex = (uint)binding.Index,
                    EntityIndex = (uint)row.Entity.Index, EntityGeneration = (uint)row.Entity.Generation };
                if (row.HasBuffer<BodyForce>())
                    foreach (ref readonly BodyForce force in row.ReadBuffer<BodyForce>().AsSpan())
                    {
                        float duration = (float)Math.Max(0, Math.Min(End, force.EndTime) - Math.Max(Start, force.StartTime));
                        input.LinearImpulse += force.Force * duration; input.AngularImpulse += force.Torque * duration;
                    }
                if (row.HasBuffer<BodyImpulse>())
                    foreach (ref readonly BodyImpulse impulse in row.ReadBuffer<BodyImpulse>().AsSpan())
                        if (impulse.Time >= Start && impulse.Time < End)
                        { input.LinearImpulse += impulse.Linear; input.AngularImpulse += impulse.Angular; }
                if (input.LinearImpulse != Vector3.Zero || input.AngularImpulse != Vector3.Zero) input.Kinds |= DeviceBodyInputKinds.Momentum;
                double latest = Start;
                if (row.HasBuffer<BodyVelocityTarget>())
                    foreach (ref readonly BodyVelocityTarget target in row.ReadBuffer<BodyVelocityTarget>().AsSpan())
                        if (target.Time >= latest && target.Time < End)
                        {
                            input.TargetLinearVelocity = target.Linear; input.TargetAngularVelocity = target.Angular;
                            input.Kinds |= DeviceBodyInputKinds.Velocity; latest = target.Time;
                        }
                latest = double.NegativeInfinity;
                if (row.HasBuffer<BodyKinematicTarget>())
                    foreach (ref readonly BodyKinematicTarget target in row.ReadBuffer<BodyKinematicTarget>().AsSpan())
                        if (target.StartTime <= End && target.StartTime >= latest)
                        {
                            float amount = (float)Math.Clamp((End - target.StartTime) / (target.EndTime - target.StartTime), 0, 1);
                            input.TargetPosition = Vector3.Lerp(target.StartPose.Position, target.EndPose.Position, amount);
                            input.TargetRotation = Quaternion.Slerp(target.StartPose.Rotation, target.EndPose.Rotation, amount);
                            input.Kinds |= DeviceBodyInputKinds.Kinematic; latest = target.StartTime;
                        }
                if (input.Kinds == 0) continue;
                MemoryMarshal.Cast<byte, DeviceBodyInput>(Inputs._inputMapping.Bytes)[Inputs._count] = input;
                Inputs._indices[Inputs._count++] = binding.Index;
            }
        }
    }
}

public readonly struct WriteDeviceBodyInputBuffers(DeviceBodyInputBuffers inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.ScheduleWrite(in context, dependency);
}
public readonly struct ApplyDeviceBodyInputBuffers(DeviceBodyInputBuffers inputs, int kernel) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.ScheduleApply(in context, kernel, dependency);
}
