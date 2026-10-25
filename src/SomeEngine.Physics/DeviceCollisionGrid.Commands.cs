using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGrid
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GridParameters(uint BodyCount, uint BucketCount, uint CellCapacity, uint BinCount,
        float CellSize, uint LevelCount, uint BlockCount, uint Revision, DevicePairing Pairing, DevicePairRules Rules,
        uint Padding0 = 0, uint Padding1 = 0);

    private QueueCompletion Submit(DevicePairing pairing, DevicePairRules rules, DeviceInputTransfer transfer,
        DevicePairReadback readback)
    {
        uint revision = transfer == DeviceInputTransfer.Upload ? _uploadRevision : _deviceRevision;
        var parameters = new GridParameters((uint)Capacity.Bodies, (uint)Capacity.Buckets, (uint)Capacity.Cells,
            (uint)Capacity.Bins, Settings.CellSize, (uint)Settings.LevelCount, (uint)BlockCount, revision, pairing, rules);
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, in parameters);
            mapping.Flush(new(0, (ulong)Marshal.SizeOf<GridParameters>()));
        }
        _backend.Begin(_commands, new CommandRecordingDesc(512, 8, 512));
        UploadInputs(_commands, transfer);
        Dispatch(0, (Capacity.Buckets + ScanWidth - 1) / ScanWidth);
        Dispatch(1, BlockCount);
        Dispatch(2, BlockCount);
        Dispatch(3, BlockCount, Capacity.Bins);
        Dispatch(4, Capacity.Bins);
        Dispatch(5, 1);
        Dispatch(6, BlockCount);
        if (readback == DevicePairReadback.Pairs)
        {
            CopyResults(_binOffsets, 0, (ulong)(Capacity.Bins + 1) * 4);
            CopyResults(Pairs, (ulong)(Capacity.Bins + 1) * 4, (ulong)Capacity.Pairs * PairStride);
        }
        using RecordedCommands commands = _backend.End(_commands);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute),
            new([], [], [commands], [], []));
        Completion = completion;
        return completion;
    }

    internal void UploadInputs(CommandContext commands, DeviceInputTransfer transfer)
    {
        if (!_buffersInitialized)
        {
            foreach (var buffer in _gpuBuffers)
                _backend.Barrier(commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
            _buffersInitialized = true;
        }
        if (transfer == DeviceInputTransfer.Upload)
        {
            _inputMapping.Flush(_inputMapping.Range);
            _backend.Barrier(commands, new BufferBarrier(Bodies, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
            _backend.CopyBuffer(commands, new BufferCopy(Upload, 0, Bodies, 0, (ulong)Capacity.Bodies * InputStride));
            _backend.Barrier(commands, new BufferBarrier(Bodies, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
            _deviceRevision = _uploadRevision;
        }
    }

    private void Dispatch(int kernel, int x, int y = 1)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        _backend.Dispatch(_commands, new((uint)x, (uint)y, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }

    private void CopyResults(SomeEngine.Graphics.Buffer source, ulong offset, ulong size)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new BufferCopy(source, 0, _readback, offset, size));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }

    private void AcceptResults(in PhysicsStepContext context, DevicePairReadback readback)
    {
        if (readback == DevicePairReadback.Pairs)
        {
            using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            int offsetBytes = (Capacity.Bins + 1) * 4;
            MemoryMarshal.Cast<byte, int>(mapping.Bytes[..offsetBytes]).CopyTo(_resultOffsets);
            MemoryMarshal.Cast<byte, DeviceCollisionPair>(mapping.Bytes.Slice(offsetBytes, Count * PairStride))
                .CopyTo(_resultPairs);
        }
        Number = context.Number;
        Time = context.EndTime;
    }
}
