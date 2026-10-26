using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceContactSupportState
{
    [FieldOffset(0)] public float MinimumHeight;
    [FieldOffset(4)] public float InitialError;
    [FieldOffset(8)] public float HeightShift;
    [FieldOffset(12)] public float Reaction;
    [FieldOffset(16)] public float Load;
    [FieldOffset(20)] public uint Enabled;
    [FieldOffset(24)] public uint UpperIsSecond;
    [FieldOffset(28)] public uint LowerBody;
    [FieldOffset(32)] public Vector3 ExtraForceOnSecond;
    [FieldOffset(44)] public uint UpperBody;
    [FieldOffset(48)] public uint LoadCount;
    [FieldOffset(52)] public uint OrderIndex;
}
public readonly struct DeviceContactSupportStateSource : IDeviceAvbdStateSource<LongRangeContactSupport, DeviceContactSupportState>
{
    public DeviceContactSupportState Read(in LongRangeContactSupport component) => new()
    {
        MinimumHeight = component.MinimumHeight, InitialError = component.InitialError, HeightShift = component.HeightShift,
        Reaction = component.Reaction, Load = component.Load, Enabled = component.Enabled ? 1u : 0u,
        UpperIsSecond = component.UpperIsSecond ? 1u : 0u, ExtraForceOnSecond = component.ExtraForceOnSecond,
    };
}
public readonly record struct DeviceSupportBodyBinding(DeviceContactSupports Supports) : IComponent;
public readonly struct DeviceSupportBodyResources : IJobComponentResources<DeviceSupportBodyBinding>
{
    public static void Collect(in DeviceSupportBodyBinding component, ref JobResourceAccessBuilder resources) { }
}

/// <summary>Selected body supports, contact supports, affine load weights and local response equations.</summary>
public sealed class DeviceContactSupports : IDisposable, IDeviceConstraintSelection, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly QueryHandle _bodyRows;
    private readonly DeviceBodies _bodies;
    private readonly Buffer _bodySelection, _parameters, _bodyStates;
    private readonly BufferSrv _bodySelectionView;
    private readonly BufferCbv _parametersView;
    private readonly CommandContext _commands;
    private readonly Dictionary<string, ResourceBinding[]> _bindings = [];
    private readonly List<Buffer> _buffers = [];
    private readonly List<BufferUav> _views = [];
    private readonly JobResourceAccess[] _queryAccesses;
    private bool _initialized;

    public DeviceContactSupports(IGraphicsBackend backend, Device device, World world, QueryHandle workBodies,
        DeviceBodies bodies, DeviceConstraintData<DeviceContactConstraint> contacts, ContactSupportSettings settings, int contactsPerBody)
    {
        _backend = backend; _device = device; _world = world; _bodies = bodies;
        QueryDefinition definition = world.GetQueryDefinition(workBodies).AsFilter()
            .WithOptionalRead<DeviceBodyBinding>().WithOptionalRead<DeviceSupportBodyBinding>();
        _bodyRows = world.Query(definition);
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceSupportBodyBinding, DeviceSupportBodyResources>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, resources);
        _bodySelection = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 16, BufferUsages.ShaderRead, "Selected support body identities"), MemoryType.Upload);
        _bodySelectionView = backend.CreateBufferSrv(device, new(_bodySelection, new(0, _bodySelection.Info.Size), StructureStride: 16));
        ContactStates = new(backend, device, contacts.Capacity, "DeviceSupportContactStates");
        _bodyStates = AddBuffer("DeviceSupportBodyStates", bodies.Capacity, 32, BufferUsages.CopyDestination);
        AddBuffer("DeviceSupportBodyContacts", bodies.Capacity * contactsPerBody, 4);
        AddBuffer("DeviceSupportBodyLoads", bodies.Capacity * 3, 8);
        AddBuffer("DeviceSupportContactLoads", contacts.Capacity * 3, 8);
        AddBuffer("DeviceSupportCandidates", contacts.Capacity, 48);
        AddBuffer("DeviceSupportOrder", contacts.Capacity + 1, 4);
        AddBuffer("DeviceSupportEquations", bodies.Capacity * (contactsPerBody + 1), 96);
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Contact support settings"), MemoryType.Upload);
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        using (MappedBuffer mapping = backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            var value = new Settings(settings.UpDirection, settings.DistanceTolerance, settings.MotionTolerance,
                settings.AngularMotionTolerance, settings.StickDistance, settings.MultiplierRetention, settings.InitialErrorFraction,
                settings.RelativePivotTolerance, (uint)contactsPerBody, (uint)bodies.Capacity, (uint)contacts.Capacity);
            MemoryMarshal.Write(mapping.Bytes, in value); mapping.Flush(new(0, (ulong)Marshal.SizeOf<Settings>()));
        }
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact support buffers"));
    }
    public DeviceConstraintState<DeviceContactSupportState> ContactStates { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public InitializeDeviceContactSupportBuffers InitializeBuffers() => new(this);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        foreach (var binding in _bindings) bindings.Add(binding.Key, binding.Value);
        ContactStates.AddBindings(bindings);
        bindings.Add("ContactSupportSettings", [ResourceBinding.ConstantBuffer(_parametersView)]);
    }
    void IDeviceConstraintSelection.AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add("DeviceSupportBodySelection", [ResourceBinding.ReadOnlyBuffer(_bodySelectionView)]);
    void IDeviceConstraintSelection.AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(_queryAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_bodySelection), 0, (long)_bodySelection.Info.Size));
    }
    void IDeviceConstraintSelection.Capture()
    {
        using MappedBuffer mapping = _backend.Map(_bodySelection, MapType.Write, BufferRange.Whole);
        mapping.Bytes.Clear();
        var read = new ReadSelection(this, mapping);
        _world.ExecuteReadQuery(_bodyRows, ref read, static (scoped QueryCursor rows, ref ReadSelection state) => state.Read(rows));
        mapping.Flush(mapping.Range);
    }
    private readonly ref struct ReadSelection(DeviceContactSupports supports, MappedBuffer mapping)
    {
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.TryRead<DeviceBodyBinding>(out var body) || !ReferenceEquals(body.Bodies, supports._bodies)
                    || !row.TryRead<DeviceSupportBodyBinding>(out var binding) || !ReferenceEquals(binding.Supports, supports)) continue;
                Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes.Slice(body.Index * 16, 16));
                values[0] = (uint)row.Entity.Index; values[1] = (uint)row.Entity.Generation; values[2] = 1;
            }
        }
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        _buffers.Select(buffer => JobResourceAccess.Read(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size))
            .Append(ContactStates.GetReadAccess(scheduler)).Append(JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256)).ToArray();
    public JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) =>
        _buffers.Select(buffer => JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size))
            .Append(ContactStates.GetWriteAccess(scheduler)).Append(JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256)).ToArray();
    private Buffer AddBuffer(string name, int count, uint stride, BufferUsages extraUsages = default)
    {
        Buffer buffer = _backend.CreateBuffer(_device, new((ulong)count * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | extraUsages, name), MemoryType.DeviceLocal);
        BufferUav view = _backend.CreateBufferUav(_device, new(buffer, new(0, buffer.Info.Size), StructureStride: stride));
        _buffers.Add(buffer); _views.Add(view); _bindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        return buffer;
    }
    internal JobHandle ScheduleInitialize(Scheduler scheduler, JobHandle dependency)
    {
        var accesses = _buffers.Select(buffer => JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size)).ToList();
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
            foreach (Buffer buffer in _buffers)
            {
                if (ReferenceEquals(buffer, _bodyStates))
                {
                    _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.Copy,
                        buffer.InitialAccess, ResourceAccess.CopyDestination));
                    _backend.ClearBuffer(_commands, buffer, new(0, buffer.Info.Size));
                    _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                        ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
                }
                else _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
            }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Settings(Vector3 Up, float DistanceTolerance, float MotionTolerance, float AngularMotionTolerance,
        float StickDistance, float Retention, float InitialErrorFraction, float RelativePivotTolerance, uint ContactsPerBody,
        uint Bodies, uint Contacts);
    public void Dispose()
    {
        _world.ReleaseQuery(_bodyRows); _commands.Dispose(); _bodySelectionView.Dispose(); _bodySelection.Dispose();
        _parametersView.Dispose(); _parameters.Dispose(); ContactStates.Dispose();
        foreach (BufferUav view in _views) view.Dispose();
        foreach (Buffer buffer in _buffers) buffer.Dispose();
    }
}
public readonly struct InitializeDeviceContactSupportBuffers(DeviceContactSupports supports) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => supports.ScheduleInitialize(WorldJobs.GetScheduler(context.World), dependency);
}
