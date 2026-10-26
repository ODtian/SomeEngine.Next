using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePoseSources
{
    private readonly List<StateCopy> _stateCopies = [];
    private Buffer? _stateCopiesBuffer;

    private void CreateStateCopies()
    {
        ulong bytes = 0;
        foreach (SourceRow row in _order)
        {
            if (row.Reset || row.State.Sources is not { } source) continue;
            GpuPoseSourceState state = row.State;
            ulong count = (ulong)row.Range.Count;
            Add(source.Models, (ulong)state.Range.Start * 64, Models, (ulong)row.Range.Start * 64, count * 64);
            Add(source._historyCurrent, source.Models.Info.Size + (ulong)state.Range.Start * 64,
                _historyCurrent, Models.Info.Size + (ulong)row.Range.Start * 64, count * 64);
            Add(source._historyPrevious, source.Models.Info.Size + (ulong)state.Range.Start * 64,
                _historyPrevious, Models.Info.Size + (ulong)row.Range.Start * 64, count * 64);
            Add(source._playback, (ulong)state.PlaybackIndex * 64, _playback, (ulong)row.Index * 64, 64);
            int blendCount = row.Input.Blend?.Steps.Length ?? 0;
            if (blendCount == 0) continue;
            Add(source._blendStates, (ulong)state.BlendStateStart * 32, _blendStates, (ulong)row.BlendStateStart * 32, (ulong)blendCount * 32);
            Add(source._blendLocals, (ulong)state.BlendLocalStart * 48, _blendLocals, (ulong)row.BlendLocalStart * 48, (ulong)blendCount * count * 48);
        }
        if (bytes > 0)
            _stateCopiesBuffer = Keep(_backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource | BufferUsages.CopyDestination,
                "Pose range copies"), MemoryType.DeviceLocal));

        void Add(Buffer from, ulong start, Buffer to, ulong destination, ulong size)
        {
            if (ReferenceEquals(from, to) && start == destination) return;
            _stateCopies.Add(new(from, start, to, destination, size, bytes));
            bytes += size;
        }
    }

    private void AddStateCopyAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        if (_stateCopiesBuffer is not { } buffer) return;
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        foreach (StateCopy copy in _stateCopies)
            accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(copy.From), (long)copy.Start, (long)copy.Size));
    }

    private void CopyStateRanges()
    {
        if (_stateCopiesBuffer is not { } temporary) return;
        Buffer[] sources = _stateCopies.Select(copy => copy.From).Distinct().ToArray();
        Buffer[] destinations = _stateCopies.Select(copy => copy.To).Distinct().ToArray();
        foreach (Buffer buffer in sources)
            _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.Barrier(_commands, new BufferBarrier(temporary, temporary.InitialSync, PipelineSync.Copy,
            temporary.InitialAccess, ResourceAccess.CopyDestination));
        foreach (StateCopy copy in _stateCopies)
            _backend.CopyBuffer(_commands, new(copy.From, copy.Start, temporary, copy.Temporary, copy.Size));
        foreach (Buffer buffer in sources)
            _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        foreach (Buffer buffer in destinations)
            _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _backend.Barrier(_commands, new BufferBarrier(temporary, PipelineSync.Copy, PipelineSync.Copy,
            ResourceAccess.CopyDestination, ResourceAccess.CopySource));
        foreach (StateCopy copy in _stateCopies)
            _backend.CopyBuffer(_commands, new(temporary, copy.Temporary, copy.To, copy.Destination, copy.Size));
        foreach (Buffer buffer in destinations)
            _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }

    private void ReleaseStateCopies()
    {
        if (_stateCopiesBuffer is not { } buffer) return;
        _objects.Remove(buffer); buffer.Dispose();
        _stateCopiesBuffer = null; _stateCopies.Clear();
    }

    private readonly record struct StateCopy(Buffer From, ulong Start, Buffer To, ulong Destination, ulong Size, ulong Temporary);
}
