using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
        private readonly QueryHandle _members;
        private readonly JobResourceAccess[] _accesses;
        private readonly BodyResultSource[] _sources;
        private readonly Dictionary<int, int> _ordinals = [];
        private int _count;
        internal QueryDefinition Definition { get; }

        internal SelectedResultRanges(DeviceBodyResults results, World world, QueryHandle members)
        {
            _results = results; _world = world; _sources = new BodyResultSource[results.States.Length];
            Definition = world.GetQueryDefinition(members).AsFilter().WithOptionalRead<DeviceBodyBinding>();
            _members = world.Query(Definition);
            var resources = new QueryReadResources();
            resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
            _accesses = new JobResourceAccess[QueryJobAccess.Count(Definition)];
            QueryJobAccess.CopyAccesses(world, Definition, _accesses, resources);
        }

        internal bool TryGetOrdinal(int index, Entity entity, out int ordinal) =>
            _ordinals.TryGetValue(index, out ordinal) && _sources[ordinal].Entity == entity;

        internal JobHandle Schedule(ResultRows rows, int rowsPerPacket, JobHandle dependency)
        {
            Scheduler scheduler = WorldJobs.GetScheduler(_world);
            JobResourceAccess[] accesses = [.. _accesses, JobResourceAccess.Write(scheduler.GetResourceToken(this))];
            return scheduler.Schedule(new Capture(this, rows, scheduler, rowsPerPacket), accesses, dependency);
        }

        private void CaptureMembers()
        {
            _count = 0; _ordinals.Clear();
            SelectedResultRanges selected = this;
            _world.ExecuteReadQuery(_members, ref selected, static (scoped QueryCursor query, ref SelectedResultRanges selected) =>
            {
                foreach (QueryRow row in query.Rows)
                    if (row.TryRead<DeviceBodyBinding>(out var binding) && ReferenceEquals(binding.Bodies, selected._results.Bodies))
                        selected._sources[selected._count++] = new(binding.Index, row.Entity);
            });
            _sources.AsSpan(0, _count).Sort(static (first, second) => first.Index.CompareTo(second.Index));
            for (int ordinal = 0; ordinal < _count; ordinal++) _ordinals.Add(_sources[ordinal].Index, ordinal);
        }

        private readonly struct Capture(SelectedResultRanges selected, ResultRows rows, Scheduler scheduler, int rowsPerPacket) : IJob
        {
            public void Execute()
            {
                selected.CaptureMembers();
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
                JobResourceAccess[] apply = [.. rows.Accesses, JobResourceAccess.Read(scheduler.GetResourceToken(selected)),
                    JobResourceAccess.Read(scheduler, results.States), JobResourceAccess.Write(scheduler.GetResourceToken(rows))];
                scheduler.Schedule(new CaptureResults(results, rows, scheduler, rowsPerPacket), apply, mapped);
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
            public QueueCompletion Submit() => selection.Submit();
        }
        private readonly record struct BodyResultSource(int Index, Entity Entity);
        public void Dispose() => _world.ReleaseQuery(_members);
    }
}
