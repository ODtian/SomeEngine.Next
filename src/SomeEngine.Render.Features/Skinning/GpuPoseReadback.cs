using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>CPU-owned model matrices copied from one GPU section after its rendering submission.</summary>
public sealed record GpuPoseReadback(uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
    ReadOnlyMemory<Matrix4x4> Current, ReadOnlyMemory<Matrix4x4> Previous);

public sealed partial class SkinningFeature
{
    private readonly List<Readback> _readbacks = [];

    /// <summary>Requests the next submitted current/previous pair for an extracted model section.</summary>
    public void RequestReadback(in RenderPoseSection section, Action<GpuPoseReadback> receive) =>
        _readbacks.Add(new(section, receive));

    private void RecordReadbacks(ref RenderGraphFrame frame)
    {
        foreach (Readback readback in _readbacks)
        {
            if (readback.Buffer is not null) continue;
            ulong bytes = (ulong)readback.Section.Range.Count * 64;
            readback.Buffer = _backend.CreateBuffer(_device, new BufferDesc(bytes * 2, BufferUsages.CopyDestination), MemoryType.Readback);
            GraphBufferId output = frame.Import(readback.Buffer,
                [new(BufferRange.Whole, readback.Buffer.InitialSync, readback.Buffer.InitialAccess, ResourceContentState.Undefined)]);
            var data = new ReadbackCopy(_currentBones, _previousBones, output,
                new BufferRange((ulong)readback.Section.Range.Start * 64, bytes));
            readback.Pass = frame.AddCopyPass("Read model pose section", PassQueueSelection.AnyOfType(QueueType.Graphics), data,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref ReadbackCopy data) =>
                {
                    pass.Read(data.Current, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Previous, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in ReadbackCopy data) =>
                {
                    BufferRegion current = commands.GetBufferRegion(data.Current), previous = commands.GetBufferRegion(data.Previous);
                    SomeEngine.Graphics.Buffer output = commands.GetBuffer(data.Output);
                    commands.CopyBuffer(new(current.Buffer, current.Range.Offset + data.Range.Offset, output, 0, data.Range.Size));
                    commands.CopyBuffer(new(previous.Buffer, previous.Range.Offset + data.Range.Offset, output, data.Range.Size, data.Range.Size));
                });
            frame.OrderAfter(readback.Pass, CompletePass);
        }
    }

    private void AcceptReadbacks(RenderGraphFrameResult result)
    {
        foreach (Readback readback in _readbacks)
            if (result.TryGetPassCompletion(readback.Pass, out QueueCompletion completion)) readback.Completion = completion;
    }

    /// <summary>Waits for requested GPU copies and delivers independently retained matrices.</summary>
    public void CompleteReadbacks()
    {
        for (int index = 0; index < _readbacks.Count;)
        {
            Readback readback = _readbacks[index];
            if (readback.Completion == default) { index++; continue; }
            if (_backend.WaitCpu(readback.Completion, TimeSpan.FromSeconds(60)) != WaitStatus.Completed)
                throw new TimeoutException("Model pose readback did not complete.");
            using SomeEngine.Graphics.Buffer buffer = readback.Buffer!;
            using MappedBuffer mapping = _backend.Map(buffer, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes);
            int count = readback.Section.Range.Count;
            var result = new GpuPoseReadback(readback.Section.Starts, readback.Section.InputNumber,
                readback.Section.CalculationTime, readback.Section.SampleTime, matrices[..count].ToArray(), matrices[count..].ToArray());
            _readbacks.RemoveAt(index);
            readback.Receive(result);
        }
    }

    private readonly record struct ReadbackCopy(GraphBufferInput Current, GraphBufferInput Previous, GraphBufferId Output, BufferRange Range);
    private sealed class Readback(RenderPoseSection section, Action<GpuPoseReadback> receive)
    {
        internal readonly RenderPoseSection Section = section;
        internal readonly Action<GpuPoseReadback> Receive = receive;
        internal SomeEngine.Graphics.Buffer? Buffer;
        internal GraphPassId Pass;
        internal QueueCompletion Completion;
    }
}
