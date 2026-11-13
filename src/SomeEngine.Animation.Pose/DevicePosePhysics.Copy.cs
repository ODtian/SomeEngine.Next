using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePosePhysics
{
    private CommandContext? _copyCommands;
    private DevicePosePhysics? _copySource;
    private readonly List<(Entity Body, int Index)> _bodyCopies = [];
    private readonly List<(Entity Constraint, int Index, bool Bend)> _constraintCopies = [];

    internal void ContinueFrom(DevicePosePhysics source)
    {
        using var structure = _world.OpenStructureWrite();
        _copySource = source;
        _bodyCount = source._bodyCount; _lengthCount = source._lengthCount; _bendCount = source._bendCount;
        CopyFree(source._freeBodies, _freeBodies); CopyFree(source._freeLengths, _freeLengths); CopyFree(source._freeBends, _freeBends);
        foreach (var entry in source._chains)
        {
            _chains.Add(entry.Key, entry.Value);
            using (RelationRead bodies = ConstraintBodies.GetBodies(_world, entry.Value))
                foreach (RelationEntry body in bodies.Entries)
                    _bodyCopies.Add((body.Other, _world.Read<DeviceBodyBinding>(body.Other).Index));
            using (var lengths = _world.ReadBuffer<GpuPoseLengthLink>(entry.Value))
                foreach (GpuPoseLengthLink link in lengths.AsSpan())
                    _constraintCopies.Add((link.Length.Entity, _world.Read<DeviceConstraintBinding<DeviceLengthConstraint>>(link.Length.Entity).Index, false));
            using (var bends = _world.ReadBuffer<GpuPoseBendLink>(entry.Value))
                foreach (GpuPoseBendLink link in bends.AsSpan())
                    _constraintCopies.Add((link.Bend, _world.Read<DeviceConstraintBinding<DeviceBendConstraint>>(link.Bend).Index, true));
        }
        source._chains.Clear();
        _copyCommands = _backend.CreateCommandContext(_device, new(QueueType.Compute, 0, 1, Label: "Continue pose constraint state"));

        static void CopyFree(Stack<int> source, Stack<int> destination)
        { foreach (int index in source.Reverse()) destination.Push(index); }
    }

    internal JobHandle ScheduleContinuation(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_copySource is not { } source) return dependency;
        Scheduler scheduler = _world.Execution!.Scheduler!;
        dependency = Bodies.CopyFrom(source.Bodies).Schedule(in context, dependency);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new ConstraintCopy(this, source),
            [source.LengthMultipliers.GetReadAccess(scheduler), source.BendMultipliers.GetReadAccess(scheduler),
                JobResourceAccess.Read(scheduler.GetResourceToken(source._sourceModels), 0, (long)source._sourceModels.Info.Size),
                LengthMultipliers.GetWriteAccess(scheduler), BendMultipliers.GetWriteAccess(scheduler),
                JobResourceAccess.Write(scheduler.GetResourceToken(_sourceModels), 0, (long)_sourceModels.Info.Size),
                JobResourceAccess.Write(scheduler.GetResourceToken(_copyCommands!))], dependency);
        return scheduler.Schedule(new WriteContinuedBindings(this),
            [.. ((WorldJobs)_world.Execution!).StructureWrite, JobResourceAccess.Write(scheduler.GetResourceToken(this))], copied);
    }

    private QueueCompletion CopyConstraints(DevicePosePhysics source)
    {
        _backend.Begin(_copyCommands!, new(0, 0, 8));
        Copy(source.LengthMultipliers.States, LengthMultipliers.States);
        Copy(source.BendMultipliers.States, BendMultipliers.States);
        Copy(source._sourceModels, _sourceModels);
        _initialized = true;
        using RecordedCommands recorded = _backend.End(_copyCommands!);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));

        void Copy(Buffer from, Buffer to)
        {
            _backend.Barrier(_copyCommands!, new BufferBarrier(from, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            _backend.Barrier(_copyCommands!, new BufferBarrier(to, to.InitialSync, PipelineSync.Copy,
                to.InitialAccess, ResourceAccess.CopyDestination));
            _backend.CopyBuffer(_copyCommands!, new(from, 0, to, 0, from.Info.Size));
            _backend.Barrier(_copyCommands!, new BufferBarrier(from, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_copyCommands!, new BufferBarrier(to, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
    }

    private readonly struct ConstraintCopy(DevicePosePhysics destination, DevicePosePhysics source) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => destination._backend;
        public Device Device => destination._device;
        public QueueCompletion Submit() => destination.CopyConstraints(source);
    }
    private readonly struct WriteContinuedBindings(DevicePosePhysics physics) : IJob
    {
        public void Execute()
        {
            using var batch = physics._world.OpenWriteBatch();
            foreach (var body in physics._bodyCopies)
                physics._world.Set(body.Body, new DeviceBodyBinding(physics.Bodies, body.Index));
            foreach (var constraint in physics._constraintCopies)
                if (constraint.Bend)
                {
                    physics._world.Set(constraint.Constraint, new DeviceConstraintBinding<DeviceBendConstraint>(physics.Bends, constraint.Index));
                    physics._world.Set(constraint.Constraint, new DeviceXpbdState<float>(physics.BendMultipliers));
                }
                else
                {
                    physics._world.Set(constraint.Constraint, new DeviceConstraintBinding<DeviceLengthConstraint>(physics.Lengths, constraint.Index));
                    physics._world.Set(constraint.Constraint, new DeviceXpbdState<float>(physics.LengthMultipliers));
                }
            physics._bodyCopies.Clear(); physics._constraintCopies.Clear(); physics._copySource = null;
        }
    }

}
