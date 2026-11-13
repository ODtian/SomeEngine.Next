using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Physics;

public readonly record struct DeviceGraphParallelKernels(ComputePipelineDesc Initialize, ComputePipelineDesc Connect,
    ComputePipelineDesc Compress, ComputePipelineDesc ScanDegrees, ComputePipelineDesc OffsetBlocks, ComputePipelineDesc AddDegreeOffsets, ComputePipelineDesc WriteAdjacency,
    ComputePipelineDesc ResetColorProgress, ComputePipelineDesc ProposeBodyColors, ComputePipelineDesc ResolveBodyColors,
    ComputePipelineDesc ProposeRowColors, ComputePipelineDesc ResolveRowColors, ComputePipelineDesc CountColors,
    ComputePipelineDesc OffsetGroups, ComputePipelineDesc CountRows, ComputePipelineDesc OffsetRanges, ComputePipelineDesc WriteWork)
{
    public ComputePipelineDesc? PrepareColors { get; init; }
    internal ComputePipelineDesc[] Descriptions(ComputePipelineDesc writeRows) =>
        [writeRows, Initialize, Connect, Compress, ScanDegrees, OffsetBlocks, AddDegreeOffsets, WriteAdjacency, ResetColorProgress,
            ProposeBodyColors, ResolveBodyColors, ProposeRowColors, ResolveRowColors, CountColors, OffsetGroups, CountRows, OffsetRanges, WriteWork,
            .. PrepareColors is { } coloring ? new[] { coloring } : Array.Empty<ComputePipelineDesc>()];
}

public sealed partial class DeviceConstraintGraph
{
    private enum GraphKernel
    {
        Initialize = 1, Connect, Compress, ScanDegrees, OffsetBlocks, AddDegreeOffsets, WriteAdjacency, ResetColorProgress,
        ProposeBodyColors, ResolveBodyColors, ProposeRowColors, ResolveRowColors, CountColors, OffsetGroups, CountRows, OffsetRanges, WriteWork,
    }
    private async ValueTask BuildParallelAsync(bool activeWorkOnly = false)
    {
        int bodyGroups = (_bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup;
        int rowGroups = (RowCount + _threadsPerGroup - 1) / _threadsPerGroup;
        BeginParallelGraph();
        if (activeWorkOnly)
        {
            DispatchGraphKernel(GraphKernel.Initialize, Math.Max(bodyGroups, rowGroups));
            DispatchGraphKernel(GraphKernel.Compress, bodyGroups);
        }
        else RecordParallelConnectivity();
        int rounds = 1;
        bool colorBodies = true, colorRows = true;
        if (_parallel!.Value.PrepareColors.HasValue)
        {
            DispatchGraphKernel(18, Math.Max(bodyGroups, rowGroups));
            await SubmitGraphAndReadCountsAsync().ConfigureAwait(false);
            colorBodies = _colorProgress[0] != 0; colorRows = _colorProgress[1] != 0;
            _backend.Begin(_commands, new(0, 0, 32));
        }
        while (colorBodies || colorRows)
        {
            for (int round = 0; round < rounds; round++)
            {
                DispatchGraphKernel(GraphKernel.ResetColorProgress, 1);
                if (colorBodies)
                {
                    DispatchGraphKernel(GraphKernel.ProposeBodyColors, bodyGroups);
                    DispatchGraphKernel(GraphKernel.ResolveBodyColors, bodyGroups);
                }
                if (colorRows)
                {
                    DispatchGraphKernel(GraphKernel.ProposeRowColors, rowGroups);
                    DispatchGraphKernel(GraphKernel.ResolveRowColors, rowGroups);
                }
            }
            await SubmitGraphAndReadCountsAsync().ConfigureAwait(false);
            colorBodies = _colorProgress[0] != 0; colorRows = _colorProgress[1] != 0;
            _backend.Begin(_commands, new(0, 0, 32));
            if (!colorBodies && !colorRows) break;
            rounds = Math.Min(rounds * 2, Math.Max(_bodies.Capacity, RowCount));
        }
        DispatchGraphKernel(GraphKernel.CountColors, Math.Max(bodyGroups, rowGroups));
        DispatchGraphKernel(GraphKernel.OffsetGroups, 1);
        DispatchGraphKernel(GraphKernel.CountRows, rowGroups);
        DispatchGraphKernel(GraphKernel.OffsetRanges, 1);
        DispatchGraphKernel(GraphKernel.WriteWork, Math.Max(bodyGroups, rowGroups));
        await SubmitGraphAndReadCountsAsync().ConfigureAwait(false);
    }
    private void BeginParallelGraph()
    {
        _backend.Begin(_commands, new(0, 0, 32));
        Contacts?.Initialize(_backend, _commands); _activeContacts?.BeginWrite(_backend, _commands);
        if (!_initialized)
            foreach (var buffer in _buffers)
                if (Memory?.Contains(buffer) != true) _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
    }
    private void RecordParallelConnectivity()
    {
        int bodyGroups = (_bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup;
        int rowGroups = (RowCount + _threadsPerGroup - 1) / _threadsPerGroup;
        DispatchGraphKernel(0, rowGroups);
        DispatchGraphKernel(GraphKernel.Initialize, Math.Max(bodyGroups, rowGroups));
        DispatchGraphKernel(GraphKernel.Connect, rowGroups);
        DispatchGraphKernel(GraphKernel.Compress, bodyGroups);
        DispatchGraphKernel(GraphKernel.ScanDegrees, (_bodies.Capacity + 255) / 256);
        DispatchGraphKernel(GraphKernel.OffsetBlocks, 1);
        DispatchGraphKernel(GraphKernel.AddDegreeOffsets, bodyGroups);
        DispatchGraphKernel(GraphKernel.WriteAdjacency, rowGroups);
    }
    private void DispatchGraphKernel(GraphKernel kernel, int groups) => DispatchGraphKernel((int)kernel, groups);
    private void DispatchGraphKernel(int kernel, int groups)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        if (groups != 0) _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(groups));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    private async ValueTask SubmitGraphAndReadCountsAsync()
    {
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.Barrier(_commands, new BufferBarrier(_buildProgress!, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_countBuffer, 0, _readback, 0, 20));
        _backend.CopyBuffer(_commands, new(_buildProgress!, 0, _readback, 20, 16));
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(_buildProgress!, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        QueueCompletion completed = SubmitGraphCommands();
        await _backend.WaitAsync(completed).ConfigureAwait(false);
        using (MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<uint> counts = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            counts[..5].CopyTo(_counts); counts.Slice(5, 4).CopyTo(_colorProgress);
        }
        _backend.CollectCompleted(_device);
    }
    private QueueCompletion SubmitGraphCommands()
    {
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completed = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        return completed;
    }
}
