using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodyResults
{
    private bool TryGetResultIndex(int bodyIndex, Entity entity, out int ordinal)
    {
        if (_selection is { } selection) return selection.TryGetOrdinal(bodyIndex, entity, out ordinal);
        ordinal = bodyIndex;
        return true;
    }

    private sealed class SelectedResultRanges : IDisposable
    {
        private readonly DeviceBodyResults _results;
        private readonly World _world;
        internal readonly Query Members;
        private readonly SystemRunner _runner;
        private readonly BodyResultSource[] _sources;
        private readonly Dictionary<int, int> _ordinals = [];
        private int _count;
        private readonly PhysicsQueryWindow _window;
        private ulong _baseline;

        internal SelectedResultRanges(DeviceBodyResults results, World world, Query members)
        {
            _results = results; _world = world; _sources = new BodyResultSource[results.States.Length];
            Members = members.WithOptionalRead<DeviceBodyBinding>();
            _runner = new(world); _window = new(Members);
        }

        internal bool TryGetOrdinal(int index, Entity entity, out int ordinal) =>
            _ordinals.TryGetValue(index, out ordinal) && _sources[ordinal].Entity == entity;

        internal JobHandle Schedule(PhysicsStepContext context, ResultRows rows, int rowsPerPacket, JobHandle dependency, bool apply)
        {
            Scheduler scheduler = _world.Execution!.Scheduler!;
            JobHandle reset = scheduler.Schedule(new Reset(this, context), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
            JobHandle captured = _runner.Schedule(Members.Since(_window.LastVersion), new CaptureMembers(this), reset, parallel: false);
            return scheduler.Schedule(new Capture(this, rows, scheduler, apply),
                [JobResourceAccess.Write(scheduler.GetResourceToken(this))], captured);
        }
        private readonly struct Reset(SelectedResultRanges selected, PhysicsStepContext context) : IJob
        {
            public void Execute()
            {
                selected._baseline = selected._window.Begin(context);
                selected._count = 0; selected._ordinals.Clear();
            }
        }
        private readonly struct CaptureMembers(SelectedResultRanges selected) : IChunkAction
        {
            private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true)];
            public static ReadOnlySpan<ChunkAccess> Components => Fields;
            public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(selected);
            public void Execute(SystemChunk rows)
            {
                ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
                for (int row = 0; row < rows.Count; row++)
                    if (ReferenceEquals(bindings[row].Bodies, selected._results.Bodies))
                        selected._sources[selected._count++] = new(bindings[row].Index, rows.Entities[row]);
                selected._window.Accept(selected._world.Version);
            }
        }

        private readonly struct Capture(SelectedResultRanges selected, ResultRows rows, Scheduler scheduler, bool apply) : IJob
        {
            public void Execute()
            {
                selected._sources.AsSpan(0, selected._count).Sort(static (first, second) => first.Index.CompareTo(second.Index));
                for (int ordinal = 0; ordinal < selected._count; ordinal++)
                    selected._ordinals.Add(selected._sources[ordinal].Index, ordinal);
                rows.Baseline = selected._baseline;
                DeviceBodyResults results = selected._results;
                var accesses = new List<JobResourceAccess>
                {
                    JobResourceAccess.Read(scheduler.GetResourceToken(selected)),
                    JobResourceAccess.Write(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                    JobResourceAccess.Write(scheduler.GetResourceToken(results._commands)),
                };
                int first = 0;
                while (first < selected._count)
                {
                    int last = selected.LastContiguous(first);
                    accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(results.Bodies.States),
                        (long)selected._sources[first].Index * DeviceBodies.StateStride, (last - first + 1L) * DeviceBodies.StateStride));
                    first = last + 1;
                }
                JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new CopySelection(selected), accesses.ToArray(), default);
                JobResourceAccess[] mapping = [JobResourceAccess.Read(scheduler.GetResourceToken(selected)),
                    JobResourceAccess.Read(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                    JobResourceAccess.Write(scheduler, results.States)];
                JobHandle mapped = scheduler.Schedule(new Map(results, selected._count), mapping, copied);
                if (apply) results.ScheduleApply(rows, scheduler, mapped);
                else scheduler.ScheduleFinally(new Collect(results), default, mapped);
            }
        }

        private int LastContiguous(int first)
        {
            int last = first;
            while (last + 1 < _count && _sources[last + 1].Index == _sources[last].Index + 1) last++;
            return last;
        }
        private QueueCompletion Submit()
        {
            IGraphicsBackend backend = _results._backend;
            backend.Begin(_results._commands, new(0, 0, 8));
            if (_count != 0)
            {
                backend.Barrier(_results._commands, new BufferBarrier(_results.Bodies.States, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                int first = 0;
                while (first < _count)
                {
                    int last = LastContiguous(first);
                    backend.CopyBuffer(_results._commands, new(_results.Bodies.States,
                        (ulong)_sources[first].Index * DeviceBodies.StateStride, _results._readback,
                        (ulong)first * DeviceBodies.StateStride, (ulong)(last - first + 1) * DeviceBodies.StateStride));
                    first = last + 1;
                }
                backend.Barrier(_results._commands, new BufferBarrier(_results.Bodies.States, PipelineSync.Copy, PipelineSync.ComputeShading,
                    ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            }
            using RecordedCommands recorded = backend.End(_results._commands);
            return backend.Submit(backend.GetQueue(_results._device, QueueType.Compute), new([], [], [recorded], [], []));
        }
        private readonly struct CopySelection(SelectedResultRanges selection) : IDevicePhysicsSubmission
        {
            public IGraphicsBackend Backend => selection._results._backend;
            public Device Device => selection._results._device;
            public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
            public QueueCompletion Submit() => selection.Submit();
        }
        private readonly record struct BodyResultSource(int Index, Entity Entity);
        public void Dispose() { }
    }
}
