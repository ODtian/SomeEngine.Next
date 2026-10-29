using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
        _copySource = source;
        _bodyCount = source._bodyCount; _lengthCount = source._lengthCount; _bendCount = source._bendCount;
        CopyFree(source._freeBodies, _freeBodies); CopyFree(source._freeLengths, _freeLengths); CopyFree(source._freeBends, _freeBends);
        foreach (var entry in source._chains)
        {
            _chains.Add(entry.Key, entry.Value);
            var capture = (Physics: this, Chain: entry.Value);
            _world.ExecuteReadQuery(source._chainRows, ref capture, static (scoped QueryCursor rows, ref (DevicePosePhysics Physics, Entity Chain) capture) =>
            {
                rows.TryGetRow(capture.Chain, out QueryRow chain);
                foreach (ConstraintBodyLink link in chain.ReadBuffer<ConstraintBodyLink>().AsSpan())
                {
                    Entity body = ConstraintBodies.GetBody(in link, rows);
                    rows.TryGetRow(body, out QueryRow member);
                    capture.Physics._bodyCopies.Add((body, member.Read<DeviceBodyBinding>().Index));
                }
                foreach (GpuPoseLengthLink link in chain.ReadBuffer<GpuPoseLengthLink>().AsSpan())
                {
                    rows.TryGetRow(link.Length.Entity, out QueryRow row);
                    capture.Physics._constraintCopies.Add((link.Length.Entity, row.Read<DeviceConstraintBinding<DeviceLengthConstraint>>().Index, false));
                }
                foreach (GpuPoseBendLink link in chain.ReadBuffer<GpuPoseBendLink>().AsSpan())
                {
                    rows.TryGetRow(link.Bend, out QueryRow row);
                    capture.Physics._constraintCopies.Add((link.Bend, row.Read<DeviceConstraintBinding<DeviceBendConstraint>>().Index, true));
                }
            });
        }
        source._chains.Clear();
        _copyCommands = _backend.CreateCommandContext(_device, new(QueueType.Compute, 0, 1, Label: "Continue pose constraint state"));

        static void CopyFree(Stack<int> source, Stack<int> destination)
        { foreach (int index in source.Reverse()) destination.Push(index); }
    }

    internal JobHandle ScheduleContinuation(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_copySource is not { } source) return dependency;
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        dependency = Bodies.CopyFrom(source.Bodies).Schedule(in context, dependency);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new ConstraintCopy(this, source),
            [source.LengthMultipliers.GetReadAccess(scheduler), source.BendMultipliers.GetReadAccess(scheduler),
                JobResourceAccess.Read(scheduler.GetResourceToken(source._sourceModels), 0, (long)source._sourceModels.Info.Size),
                LengthMultipliers.GetWriteAccess(scheduler), BendMultipliers.GetWriteAccess(scheduler),
                JobResourceAccess.Write(scheduler.GetResourceToken(_sourceModels), 0, (long)_sourceModels.Info.Size),
                JobResourceAccess.Write(scheduler.GetResourceToken(_copyCommands!))], dependency);
        return scheduler.Schedule(new WriteContinuedBindings(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], copied);
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
            Scheduler scheduler = WorldJobs.GetScheduler(physics._world);
            var commands = new JobCommandBuffer(physics._world, 1);
            commands.Schedule(0, new ApplyContinuedBindings(physics), [JobResourceAccess.Write(scheduler.GetResourceToken(physics))]);
            JobHandle applied = commands.SchedulePlayback();
            scheduler.ScheduleFinally(new ReleaseCopyCommands(commands), default, applied);
        }
    }
    private readonly struct ApplyContinuedBindings(DevicePosePhysics physics) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            foreach (var body in physics._bodyCopies)
                commands.Replace(body.Body, new DeviceBodyBinding(physics.Bodies, body.Index));
            foreach (var constraint in physics._constraintCopies)
                if (constraint.Bend)
                {
                    commands.Replace(constraint.Constraint, new DeviceConstraintBinding<DeviceBendConstraint>(physics.Bends, constraint.Index));
                    commands.Replace(constraint.Constraint, new DeviceXpbdState<float>(physics.BendMultipliers));
                }
                else
                {
                    commands.Replace(constraint.Constraint, new DeviceConstraintBinding<DeviceLengthConstraint>(physics.Lengths, constraint.Index));
                    commands.Replace(constraint.Constraint, new DeviceXpbdState<float>(physics.LengthMultipliers));
                }
            physics._bodyCopies.Clear(); physics._constraintCopies.Clear(); physics._copySource = null;
        }
    }
    private readonly struct ReleaseCopyCommands(JobCommandBuffer commands) : IJob
    { public void Execute() => commands.Dispose(); }
}
