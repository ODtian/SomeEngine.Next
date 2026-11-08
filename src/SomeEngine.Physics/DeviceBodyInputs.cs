using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Transfers edited component fields and new entities into their continuing device states.</summary>
public sealed class DeviceBodyInputs : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _members;
    private readonly DeviceBodies _bodies;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly List<BufferRange> _ranges = [];
    private readonly List<InputChange> _changes = [];
    private InputPacket[] _packets = [];
    private uint _lastSystemVersion;

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

    internal object InputKey => _bodies;
    public UploadEditedDeviceBodies Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _bodies.GetInputTransferAccesses(scheduler)];
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, context.Number, context.Substep), accesses, dependency);
    }

    internal JobHandle ScheduleWrite(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _bodies.GetInputTransferAccesses(scheduler)];
        return scheduler.Schedule(new Write(this, context.Number, context.Substep, rowsPerPacket), accesses, dependency);
    }

    private QueueCompletion Submit(long number, int substep)
    {
        ReadInputs(number, substep);
        QueueCompletion complete = _bodies.SubmitInputRanges(CollectionsMarshal.AsSpan(_ranges));
        AcceptInputs();
        return complete;
    }

    private void ReadInputs(long number, int substep, bool writeAll = false, int rowsPerPacket = 256)
    {
        _ranges.Clear(); _changes.Clear();
        var inputs = (Inputs: this, Version: 0u, WriteAll: writeAll, RowsPerPacket: rowsPerPacket);
        _world.ExecuteReadQuery(_members, _lastSystemVersion, ref inputs,
            static (scoped QueryCursor rows,
                ref (DeviceBodyInputs Inputs, uint Version, bool WriteAll, int RowsPerPacket) inputs) =>
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(rows, inputs.RowsPerPacket);
            DeviceBodyInputs destination = inputs.Inputs;
            if (destination._packets.Length < packets.PacketCount)
            {
                int previous = destination._packets.Length;
                Array.Resize(ref destination._packets, Math.Max(packets.PacketCount, previous * 2));
                for (int index = previous; index < destination._packets.Length; index++) destination._packets[index] = new();
            }
            Scheduler scheduler = WorldJobs.GetScheduler(destination._world);
            packets.ExecuteParallel(new ReadInputsPacket(destination, inputs.WriteAll), destination._bodies.GetInputTransferAccesses(scheduler));
            for (int index = 0; index < packets.PacketCount; index++)
            {
                destination._ranges.AddRange(destination._packets[index].Ranges);
                destination._changes.AddRange(destination._packets[index].Changes);
            }
            inputs.Version = rows.CurrentSystemVersion;
        });
        foreach (InputChange change in _changes)
            _bodies.RecordCpuInputChange(change.Index, change.Version.Entity, change.Components, number, substep);
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
        if (merged < _ranges.Count) _ranges.RemoveRange(merged, _ranges.Count - merged);
        _lastSystemVersion = inputs.Version;
    }

    private void AcceptInputs()
    {
        foreach (InputChange change in _changes)
        {
            DeviceBodies.BodyInputVersions version = change.Version;
            _bodies.AcceptCpuVersions(change.Index, in version);
        }
    }

    private readonly record struct InputChange(int Index, DeviceBodies.BodyInputVersions Version, DeviceBodies.BodyInputComponents Components);
    private sealed class InputPacket
    {
        internal readonly List<InputChange> Changes = [];
        internal readonly List<BufferRange> Ranges = [];
    }
    private readonly struct ReadInputsPacket(DeviceBodyInputs inputs, bool writeAll) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket rows)
        {
            InputPacket output = inputs._packets[context.PacketIndex];
            output.Changes.Clear(); output.Ranges.Clear();
            if (!rows.Has<DeviceBodyBinding>()) return;
            ReadOnlySpan<uint> poseVersions = rows.ReadWriteVersions<BodyPose>(), massVersions = rows.ReadWriteVersions<BodyMass>();
            ReadOnlySpan<uint> velocityVersions = rows.ReadWriteVersions<BodyVelocity>(), stepVersions = rows.ReadWriteVersions<BodyStepState>();
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            ReadOnlySpan<BodyPose> poses = rows.Read<BodyPose>(); ReadOnlySpan<BodyMass> masses = rows.Read<BodyMass>();
            ReadOnlySpan<BodyVelocity> velocities = rows.Read<BodyVelocity>(); ReadOnlySpan<BodyStepState> steps = rows.Read<BodyStepState>();
            for (int index = 0; index < rows.Count; index++)
            {
                DeviceBodyBinding binding = bindings[index];
                if (!ReferenceEquals(binding.Bodies, inputs._bodies)) continue;
                Entity entity = rows.Entities[index];
                var version = new DeviceBodies.BodyInputVersions(entity, poseVersions[index], massVersions[index],
                    velocityVersions[index], stepVersions[index], DeviceBodies.BodyInputComponents.All);
                DeviceBodies.BodyInputVersions previous = inputs._bodies.ReadCpuVersions(binding.Index);
                DeviceBodies.BodyInputComponents changed = writeAll ? DeviceBodies.BodyInputComponents.All
                    : DeviceBodies.ChangedInputs(in version, in previous);
                if (changed == 0) continue;
                version = version with { InputVersion = previous.InputVersion + 1 };
                DeviceBodyState input = DeviceBodies.ReadInput(entity, in poses[index], in masses[index], in velocities[index], in steps[index]);
                input.InputVersion = version.InputVersion;
                inputs._bodies.WriteInput(binding.Index, in input);
                output.Changes.Add(new(binding.Index, version, changed));
                ulong start = (ulong)binding.Index * DeviceBodies.StateStride;
                if (previous.Entity != entity) { output.Ranges.Add(new(start, DeviceBodies.StateStride)); continue; }
                Add(92, 4);
                if ((changed & DeviceBodies.BodyInputComponents.Pose) != 0) { Add(0, 12); Add(16, 16); }
                if ((changed & DeviceBodies.BodyInputComponents.Mass) != 0)
                { Add(12, 4); Add(44, 4); Add(64, 12); Add(80, 12); Add(96, 12); Add(112, 12); }
                if ((changed & DeviceBodies.BodyInputComponents.Velocity) != 0) { Add(32, 12); Add(48, 12); }
                if ((changed & DeviceBodies.BodyInputComponents.Step) != 0) Add(128, 80);
                void Add(ulong offset, ulong size) => output.Ranges.Add(new(start + offset, size));
            }
        }
    }
    public void Dispose() => _world.ReleaseQuery(_members);
    private readonly struct Write(DeviceBodyInputs inputs, long number, int substep, int rowsPerPacket) : IJob
    {
        public void Execute() { inputs.ReadInputs(number, substep, writeAll: true, rowsPerPacket); inputs.AcceptInputs(); }
    }
    private readonly struct Transfer(DeviceBodyInputs inputs, long number, int substep) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._bodies.Backend;
        public Device Device => inputs._bodies.Device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => inputs.Submit(number, substep);
    }
}

public readonly struct UploadEditedDeviceBodies(DeviceBodyInputs inputs) : IDevicePhysicsStage
{
    public object InputKey => inputs.InputKey;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
