using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Current detected geometry and weighted body anchors in the selected persistent contact representation.</summary>
public sealed class DeviceContactGeometry : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceContactMatches _matches;
    private readonly DeviceConstraintData<DeviceContactConstraint> _parameters;
    private readonly DeviceCollisionDetection _detection;
    private readonly DeviceBodies _bodies;
    private readonly DeviceContactBodies _anchors;
    private readonly Buffer _rows, _settings;
    private readonly BufferSrv _rowsView, _contactsView, _bodyCountsView, _pointCountsView, _bodiesView;
    private readonly BufferCbv _settingsView;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;

    internal DeviceContactGeometry(IGraphicsBackend backend, Device device, DeviceContactMatches matches,
        DeviceConstraintData<DeviceContactConstraint> parameters, DeviceCollisionDetection detection,
        DeviceBodies bodies, DeviceContactBodies anchors, ComputePipelineDesc kernel)
    {
        _backend = backend; _device = device; _matches = matches; _parameters = parameters;
        _detection = detection; _bodies = bodies; _anchors = anchors;
        _rows = backend.CreateBuffer(device, new((ulong)detection.Capacity.Pairs * 4 * 4, BufferUsages.ShaderRead), MemoryType.Upload);
        _settings = backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload);
        _rowsView = backend.CreateBufferSrv(device, new(_rows, new(0, _rows.Info.Size), StructureStride: 4));
        _contactsView = backend.CreateBufferSrv(device, new(detection.Contacts, new(0, detection.Contacts.Info.Size), StructureStride: DeviceCollisionDetection.ContactStride));
        _bodyCountsView = backend.CreateBufferSrv(device, new(detection.ContactBodyCounts!, new(0, detection.ContactBodyCounts!.Info.Size), StructureStride: 4));
        _pointCountsView = backend.CreateBufferSrv(device, new(detection.ContactCounts, new(0, detection.ContactCounts.Info.Size), StructureStride: 4));
        _bodiesView = backend.CreateBufferSrv(device, new(detection.ContactBodies!, new(0, detection.ContactBodies!.Info.Size), StructureStride: 32));
        _settingsView = backend.CreateBufferCbv(device, new(_settings, new(0, 256)));
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceContactGeometryRows"] = [ResourceBinding.ReadOnlyBuffer(_rowsView)],
            ["DeviceMatchInputContacts"] = [ResourceBinding.ReadOnlyBuffer(_contactsView)],
            ["DeviceMatchInputBodyCounts"] = [ResourceBinding.ReadOnlyBuffer(_bodyCountsView)],
            ["DeviceMatchInputCounts"] = [ResourceBinding.ReadOnlyBuffer(_pointCountsView)],
            ["DeviceMatchInputBodies"] = [ResourceBinding.ReadOnlyBuffer(_bodiesView)],
            ["ContactGeometrySettings"] = [ResourceBinding.ConstantBuffer(_settingsView)],
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
        };
        parameters.AddBindings(bindings); anchors.AddBindings(bindings);
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Current contact geometry"));
    }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public UpdateDeviceContactGeometry Update() => new(this);
    public DeviceContactGeometryMatches Results => new(_matches);
    internal JobHandle Schedule(Scheduler scheduler, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>(_parameters.GetReadAccesses(scheduler));
        accesses.AddRange(_detection.GetReadbackAccesses(scheduler));
        accesses.AddRange([
            _matches.ReadAccess, _bodies.GetReadAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_parameters.Data), 0, (long)_parameters.Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_anchors.Values), 0, (long)_anchors.Values.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_detection.Contacts), 0, (long)_detection.Contacts.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_detection.ContactCounts), 0, (long)_detection.ContactCounts.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_detection.ContactBodyCounts!), 0, (long)_detection.ContactBodyCounts!.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_detection.ContactBodies!), 0, (long)_detection.ContactBodies!.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_rows), 0, (long)_rows.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_settings), 0, 256),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))]);
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        using (MappedBuffer mapping = _backend.Map(_rows, MapType.Write, BufferRange.Whole))
        {
            _matches.WriteParameterIndices(MemoryMarshal.Cast<byte, uint>(mapping.Bytes));
            mapping.Flush(new(0, (ulong)_detection.Count * 4 * 4));
        }
        using (MappedBuffer mapping = _backend.Map(_settings, MapType.Write, BufferRange.Whole))
        {
            Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            values[0] = (uint)_detection.Count; values[1] = (uint)_anchors.BodiesPerContact;
            mapping.Flush(new(0, 8));
        }
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, []));
        if (_detection.Count != 0) _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_detection.Count * 4 + 63) / 64));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    public void Dispose()
    {
        _commands.Dispose(); _pipeline.Dispose(); _rowsView.Dispose(); _contactsView.Dispose(); _bodyCountsView.Dispose(); _pointCountsView.Dispose(); _bodiesView.Dispose();
        _settingsView.Dispose(); _rows.Dispose(); _settings.Dispose();
    }
}
public readonly struct DeviceContactGeometryMatches(DeviceContactMatches matches) : IContactPointMatches
{
    public bool RetainsGeometry => true;
    public void AddAccesses(List<JobResourceAccess> accesses) => accesses.Add(matches.WriteAccess);
    public bool TryGetMatch(int input, out Entity point, out float distanceSquared) => matches.Read(input, out point, out distanceSquared);
    public void RecordPoint(int input, Entity point) => matches.Record(input, point);
}
public readonly struct UpdateDeviceContactGeometry(DeviceContactGeometry geometry) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => geometry.Schedule(WorldJobs.GetScheduler(context.World), dependency);
}
