using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Graphics;

namespace SomeEngine.Physics;

/// <summary>Transfers edited component fields and new entities into their continuing device states.</summary>
public sealed class DeviceBodyInputs : IDisposable
{
    private readonly World _world;
    private readonly Query _members;
    private readonly DeviceBodies _bodies;
    private readonly List<BufferRange> _ranges = [];
    private SystemRunner _runner;
    private int _rowsPerRange = 256;
    private ulong _lastSystemVersion, _observedVersion;

    public DeviceBodyInputs(World world, Query members, DeviceBodies bodies)
    {
        _world = world; _members = members; _bodies = bodies;
        _runner = new(world, _rowsPerRange);
    }

    internal object InputKey => _bodies;
    public UploadEditedDeviceBodies Upload() => new(this);

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle gathered = ScheduleInputs(in context, false, dependency);
        return ScheduleTransfer(in context, gathered);
    }

    internal JobHandle ScheduleTransfer(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = _world.Execution!.Scheduler!;
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this),
            [.. _bodies.GetInputTransferAccesses(scheduler), JobResourceAccess.Read(scheduler.GetResourceToken(this))], dependency);
    }

    internal JobHandle ScheduleWrite(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        if (_rowsPerRange != rowsPerPacket)
        {
            _rowsPerRange = rowsPerPacket;
            _runner = new(_world, rowsPerPacket);
        }
        return ScheduleInputs(in context, true, dependency);
    }

    internal JobHandle ScheduleInputs(in PhysicsStepContext context, bool writeAll, JobHandle dependency)
    {
        Scheduler scheduler = _world.Execution!.Scheduler!;
        JobHandle reset = scheduler.Schedule(new Reset(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
        JobHandle converted = _runner.Schedule(_members.Since(_lastSystemVersion),
            new ConvertInputs(this, writeAll), reset);
        JobHandle gathered = _runner.Schedule(_members.Since(_lastSystemVersion),
            new GatherInputs(this, context.Number, context.Substep, writeAll), converted, parallel: false);
        return scheduler.Schedule(new Finish(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], gathered);
    }

    private static bool Changed(SystemChunk rows, ulong since) =>
        rows.WasWritten<DeviceBodyBinding>(since) || rows.WasWritten<BodyPose>(since)
        || rows.WasWritten<BodyMass>(since) || rows.WasWritten<BodyVelocity>(since)
        || rows.WasWritten<BodyStepState>(since);

    private readonly struct ConvertInputs(DeviceBodyInputs inputs, bool writeAll) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true), ChunkAccess.Read<BodyPose>(),
                ChunkAccess.Read<BodyMass>(), ChunkAccess.Read<BodyVelocity>(), ChunkAccess.Read<BodyStepState>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;

        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Read(inputs);

        public void CollectRangeResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            int first = 0;
            while (first < bindings.Length)
            {
                DeviceBodyBinding binding = bindings[first];
                if (!ReferenceEquals(binding.Bodies, inputs._bodies)) { first++; continue; }
                int end = first + 1;
                while (end < bindings.Length && ReferenceEquals(bindings[end].Bodies, binding.Bodies)
                    && bindings[end].Index == binding.Index + end - first) end++;
                binding.Bodies.CollectInputWrites(binding.Index, end - first, ref resources);
                first = end;
            }
        }

        public void Execute(SystemChunk rows)
        {
            ulong since = inputs._lastSystemVersion;
            if (!writeAll && !Changed(rows, since)) return;
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            ReadOnlySpan<BodyPose> poses = rows.Read<BodyPose>();
            ReadOnlySpan<BodyMass> masses = rows.Read<BodyMass>();
            ReadOnlySpan<BodyVelocity> velocities = rows.Read<BodyVelocity>();
            ReadOnlySpan<BodyStepState> steps = rows.Read<BodyStepState>();
            int first = 0;
            while (first < rows.Count)
            {
                DeviceBodyBinding binding = bindings[first];
                if (!ReferenceEquals(binding.Bodies, inputs._bodies)) { first++; continue; }
                int end = first + 1;
                while (end < rows.Count && ReferenceEquals(bindings[end].Bodies, inputs._bodies)
                    && bindings[end].Index == binding.Index + end - first) end++;
                Span<DeviceBodyState> destination = inputs._bodies.InputStates(binding.Index, end - first);
                for (int row = first; row < end; row++)
                {
                    int index = bindings[row].Index;
                    var entity = rows.Entities[row];
                    DeviceBodies.BodyInputVersions previous = inputs._bodies.ReadCpuVersions(index);
                    bool initialize = previous.Entity != entity;
                    DeviceBodies.BodyInputComponents changed = writeAll || initialize
                        ? DeviceBodies.BodyInputComponents.All : DeviceBodies.BodyInputComponents.All & ~previous.Known;
                    if (rows.WasWritten<BodyPose>(row, Math.Max(since, previous.Pose))) changed |= DeviceBodies.BodyInputComponents.Pose;
                    if (rows.WasWritten<BodyMass>(row, Math.Max(since, previous.Mass))) changed |= DeviceBodies.BodyInputComponents.Mass;
                    if (rows.WasWritten<BodyVelocity>(row, Math.Max(since, previous.Velocity))) changed |= DeviceBodies.BodyInputComponents.Velocity;
                    if (rows.WasWritten<BodyStepState>(row, Math.Max(since, previous.Step))) changed |= DeviceBodies.BodyInputComponents.Step;
                    if (changed == 0) continue;
                    ulong version = inputs._world.Version;
                    var current = new DeviceBodies.BodyInputVersions(entity,
                        (changed & DeviceBodies.BodyInputComponents.Pose) != 0 ? version : previous.Pose,
                        (changed & DeviceBodies.BodyInputComponents.Mass) != 0 ? version : previous.Mass,
                        (changed & DeviceBodies.BodyInputComponents.Velocity) != 0 ? version : previous.Velocity,
                        (changed & DeviceBodies.BodyInputComponents.Step) != 0 ? version : previous.Step,
                        previous.Known | changed, previous.InputVersion + 1);
                    DeviceBodyState input = DeviceBodies.ReadInput(entity, in poses[row], in masses[row], in velocities[row], in steps[row]);
                    input.InputVersion = current.InputVersion;
                    destination[row - first] = input;
                    inputs._bodies.AcceptCpuVersions(index, in current);
                    inputs._bodies.SetInputComponents(index, changed | (initialize ? DeviceBodies.BodyInputComponents.NewEntity : 0));
                }
                first = end;
            }
        }
    }

    private readonly struct GatherInputs(DeviceBodyInputs inputs, long number, int substep, bool writeAll) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => ConvertInputs.Components;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(inputs);
            inputs._bodies.CollectCpuInputStaging(ref resources);
        }
        public void CollectRangeResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            int first = 0;
            while (first < bindings.Length)
            {
                DeviceBodyBinding binding = bindings[first];
                if (!ReferenceEquals(binding.Bodies, inputs._bodies)) { first++; continue; }
                int end = first + 1;
                while (end < bindings.Length && ReferenceEquals(bindings[end].Bodies, binding.Bodies)
                    && bindings[end].Index == binding.Index + end - first) end++;
                binding.Bodies.CollectInputComponents(binding.Index, end - first, ref resources);
                first = end;
            }
        }
        public void Execute(SystemChunk rows)
        {
            if (!writeAll && !Changed(rows, inputs._lastSystemVersion)) return;
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            List<BufferRange> ranges = inputs._ranges;
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceBodyBinding binding = bindings[row];
                if (!ReferenceEquals(binding.Bodies, inputs._bodies)) continue;
                DeviceBodies.BodyInputComponents changed = inputs._bodies.TakeInputComponents(binding.Index);
                if (changed == 0) continue;
                inputs._bodies.RecordCpuInputChange(binding.Index, rows.Entities[row],
                    changed & DeviceBodies.BodyInputComponents.All, number, substep);
                ulong start = (ulong)binding.Index * DeviceBodies.StateStride;
                if ((changed & DeviceBodies.BodyInputComponents.NewEntity) != 0)
                { inputs._ranges.Add(new(start, DeviceBodies.StateStride)); continue; }
                Add(92, 4);
                if ((changed & DeviceBodies.BodyInputComponents.Pose) != 0) { Add(0, 12); Add(16, 16); }
                if ((changed & DeviceBodies.BodyInputComponents.Mass) != 0)
                { Add(12, 4); Add(44, 4); Add(64, 12); Add(80, 12); Add(96, 12); Add(112, 12); }
                if ((changed & DeviceBodies.BodyInputComponents.Velocity) != 0) { Add(32, 12); Add(48, 12); }
                if ((changed & DeviceBodies.BodyInputComponents.Step) != 0) Add(128, 80);
                void Add(ulong offset, ulong size) => ranges.Add(new(start + offset, size));
            }
        }
    }

    private readonly struct Reset(DeviceBodyInputs inputs) : IJob
    {
        public void Execute()
        {
            inputs._ranges.Clear();
            inputs._observedVersion = inputs._world.Version;
        }
    }
    private readonly struct Finish(DeviceBodyInputs inputs) : IJob
    {
        public void Execute()
        {
            DeviceInputPages.MergeRanges(inputs._ranges);
            inputs._lastSystemVersion = inputs._observedVersion;
        }
    }
    private readonly struct Transfer(DeviceBodyInputs inputs) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._bodies.Backend;
        public Device Device => inputs._bodies.Device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => inputs._bodies.SubmitInputRanges(CollectionsMarshal.AsSpan(inputs._ranges));
    }
    public void Dispose() { }
}

public readonly struct UploadEditedDeviceBodies(DeviceBodyInputs inputs) : IDevicePhysicsStage, IPhysicsStageComposition
{
    public object InputKey => inputs.InputKey;
    public bool CanBatch => false;
    public void Append(PhysicsStageCommands commands)
    {
        commands.Schedule(new ReadFields(inputs));
        commands.Append(new TransferFields(inputs));
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
    private readonly struct ReadFields(DeviceBodyInputs inputs) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            inputs.ScheduleInputs(in context, false, dependency);
    }
    private readonly struct TransferFields(DeviceBodyInputs inputs) : IDevicePhysicsStage
    {
        public object InputKey => inputs.InputKey;
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.ScheduleTransfer(in context, dependency);
    }
}
