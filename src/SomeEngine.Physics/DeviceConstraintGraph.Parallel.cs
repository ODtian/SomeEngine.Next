using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Physics;

public readonly record struct DeviceGraphParallelKernels(ComputePipelineDesc Initialize, ComputePipelineDesc Connect,
    ComputePipelineDesc Compress, ComputePipelineDesc ScanDegrees, ComputePipelineDesc OffsetBlocks, ComputePipelineDesc AddDegreeOffsets, ComputePipelineDesc WriteAdjacency,
    ComputePipelineDesc ResetColorProgress, ComputePipelineDesc ProposeBodyColors, ComputePipelineDesc ResolveBodyColors,
    ComputePipelineDesc ProposeRowColors, ComputePipelineDesc ResolveRowColors, ComputePipelineDesc CountColors,
    ComputePipelineDesc OffsetGroups, ComputePipelineDesc CountRows, ComputePipelineDesc OffsetRanges, ComputePipelineDesc WriteWork)
{
    internal ComputePipelineDesc[] Descriptions(ComputePipelineDesc writeRows) =>
        [writeRows, Initialize, Connect, Compress, ScanDegrees, OffsetBlocks, AddDegreeOffsets, WriteAdjacency, ResetColorProgress,
            ProposeBodyColors, ResolveBodyColors, ProposeRowColors, ResolveRowColors, CountColors, OffsetGroups, CountRows, OffsetRanges, WriteWork];
}

public sealed partial class DeviceConstraintGraph
{
    private enum GraphKernel
    {
        Initialize = 1, Connect, Compress, ScanDegrees, OffsetBlocks, AddDegreeOffsets, WriteAdjacency, ResetColorProgress,
        ProposeBodyColors, ResolveBodyColors, ProposeRowColors, ResolveRowColors, CountColors, OffsetGroups, CountRows, OffsetRanges, WriteWork,
    }
    private void BuildParallelAndWait()
    {
        int bodyGroups = (_bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup;
        int rowGroups = (_capacity.Constraints + _threadsPerGroup - 1) / _threadsPerGroup;
        _backend.Begin(_commands, new(0, 0, 32));
        if (!_initialized)
            foreach (var buffer in _buffers)
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        DispatchGraphKernel(0, rowGroups);
        DispatchGraphKernel(GraphKernel.Initialize, Math.Max(bodyGroups, rowGroups));
        DispatchGraphKernel(GraphKernel.Connect, rowGroups);
        DispatchGraphKernel(GraphKernel.Compress, bodyGroups);
        DispatchGraphKernel(GraphKernel.ScanDegrees, (_bodies.Capacity + 255) / 256);
        DispatchGraphKernel(GraphKernel.OffsetBlocks, 1);
        DispatchGraphKernel(GraphKernel.AddDegreeOffsets, bodyGroups);
        DispatchGraphKernel(GraphKernel.WriteAdjacency, rowGroups);
        RecordColorRounds(bodyGroups, bodies: true);
        SubmitGraphAndReadCounts();
        while (_colorProgress[0] != 0)
        {
            _backend.Begin(_commands, new(0, 0, 32));
            RecordColorRounds(bodyGroups, bodies: true);
            SubmitGraphAndReadCounts();
        }
        _backend.Begin(_commands, new(0, 0, 32));
        RecordColorRounds(rowGroups, bodies: false);
        SubmitGraphAndReadCounts();
        while (_colorProgress[1] != 0)
        {
            _backend.Begin(_commands, new(0, 0, 32));
            RecordColorRounds(rowGroups, bodies: false);
            SubmitGraphAndReadCounts();
        }
        _backend.Begin(_commands, new(0, 0, 16));
        DispatchGraphKernel(GraphKernel.CountColors, Math.Max(bodyGroups, rowGroups));
        DispatchGraphKernel(GraphKernel.OffsetGroups, 1);
        DispatchGraphKernel(GraphKernel.CountRows, rowGroups);
        DispatchGraphKernel(GraphKernel.OffsetRanges, 1);
        DispatchGraphKernel(GraphKernel.WriteWork, Math.Max(bodyGroups, rowGroups));
        SubmitGraphAndReadCounts();
    }
    private void RecordColorRounds(int groups, bool bodies)
    {
        for (int round = 0; round < 8; round++)
        {
            DispatchGraphKernel(GraphKernel.ResetColorProgress, 1);
            DispatchGraphKernel(bodies ? GraphKernel.ProposeBodyColors : GraphKernel.ProposeRowColors, groups);
            DispatchGraphKernel(bodies ? GraphKernel.ResolveBodyColors : GraphKernel.ResolveRowColors, groups);
        }
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
    private void SubmitGraphAndReadCounts()
    {
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.Barrier(_commands, new BufferBarrier(_buildProgress!, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_countBuffer, 0, _readback, 0, 16));
        _backend.CopyBuffer(_commands, new(_buildProgress!, 0, _readback, 16, 16));
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(_buildProgress!, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completed = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        if (_backend.WaitCpu(in completed, TimeSpan.FromSeconds(60)) != WaitStatus.Completed) throw new TimeoutException();
        using (MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<uint> counts = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            counts[..4].CopyTo(_counts); counts.Slice(4, 4).CopyTo(_colorProgress);
        }
        _backend.CollectCompleted(_device);
    }
}
