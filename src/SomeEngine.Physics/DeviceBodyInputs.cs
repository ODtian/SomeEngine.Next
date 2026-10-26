using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Transfers edited component fields and new entities into their continuing device states.</summary>
public sealed class DeviceBodyInputs : IDisposable, IDevicePhysicsSubmission
{
    private readonly World _world;
    private readonly QueryHandle _members;
    private readonly DeviceBodies _bodies;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly List<BufferRange> _ranges = [];
    private readonly List<(int Index, DeviceBodies.BodyInputVersions Version)> _changes = [];

    public DeviceBodyInputs(World world, QueryHandle members, DeviceBodies bodies)
    {
        _world = world; _bodies = bodies;
        QueryDefinition definition = world.GetQueryDefinition(members).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<DeviceBodyBinding>().Read<BodyPose>().Read<BodyMass>().Read<BodyVelocity>().Read<BodyStepState>().Build());
        _members = world.Query(definition);
        var descriptions = new QueryReadResources();
        descriptions.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, descriptions);
    }

    public UploadEditedDeviceBodies Upload() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _bodies.Backend;
    Device IDevicePhysicsSubmission.Device => _bodies.Device;
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _bodies.GetInputTransferAccesses(scheduler)];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, dependency);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _ranges.Clear(); _changes.Clear();
        DeviceBodyInputs inputs = this;
        _world.ExecuteReadQuery(_members, ref inputs, static (scoped QueryCursor rows, ref DeviceBodyInputs inputs) =>
        {
            foreach (QueryRow row in rows.Rows) inputs.Read(row);
        });
        _ranges.Sort(static (first, second) => first.Offset.CompareTo(second.Offset));
        int merged = 0;
        foreach (BufferRange range in CollectionsMarshal.AsSpan(_ranges))
        {
            if (merged != 0 && _ranges[merged - 1].Offset + _ranges[merged - 1].Size == range.Offset)
            {
                BufferRange previous = _ranges[merged - 1];
                _ranges[merged - 1] = new(previous.Offset, previous.Size + range.Size);
            }
            else _ranges[merged++] = range;
        }
        QueueCompletion complete = _bodies.SubmitInputRanges(CollectionsMarshal.AsSpan(_ranges)[..merged]);
        foreach (var change in _changes) _bodies.AcceptCpuVersions(change.Index, in change.Version);
        return complete;
    }

    private void Read(scoped QueryRow row)
    {
        DeviceBodyBinding binding = row.Read<DeviceBodyBinding>();
        if (!ReferenceEquals(binding.Bodies, _bodies)) return;
        DeviceBodies.BodyInputVersions version = DeviceBodies.ReadVersions(row);
        DeviceBodies.BodyInputVersions previous = _bodies.ReadCpuVersions(binding.Index);
        DeviceBodies.BodyInputComponents changed = DeviceBodies.ChangedInputs(in version, in previous);
        if (changed == 0) return;
        _bodies.WriteInput(row, binding.Index);
        _changes.Add((binding.Index, version));
        ulong start = (ulong)binding.Index * DeviceBodies.StateStride;
        if (previous.Entity != row.Entity) { _ranges.Add(new(start, DeviceBodies.StateStride)); return; }
        if ((changed & DeviceBodies.BodyInputComponents.Pose) != 0) { Add(0, 12); Add(16, 16); }
        if ((changed & DeviceBodies.BodyInputComponents.Mass) != 0)
        { Add(12, 4); Add(44, 4); Add(64, 12); Add(80, 12); Add(96, 12); Add(112, 12); }
        if ((changed & DeviceBodies.BodyInputComponents.Velocity) != 0) { Add(32, 12); Add(48, 12); }
        if ((changed & DeviceBodies.BodyInputComponents.Step) != 0) Add(128, 80);
        void Add(ulong offset, ulong size) => _ranges.Add(new(start + offset, size));
    }
    public void Dispose() => _world.ReleaseQuery(_members);
}

public readonly struct UploadEditedDeviceBodies(DeviceBodyInputs inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
