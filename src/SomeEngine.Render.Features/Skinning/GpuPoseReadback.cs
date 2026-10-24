using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>The GPU-selected state clocks and crossfade weights accompanying a model pose.</summary>
public readonly record struct GpuPosePlayback(int State, int SourceState, float Time, float SourceTime,
    float SourceSampleTime, float TransitionWeight, bool Finished);

/// <summary>CPU-owned matrices and playback data copied together from one submitted GPU section.</summary>
public sealed record GpuPoseReadback(uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
    ReadOnlyMemory<Matrix4x4> Current, ReadOnlyMemory<Matrix4x4> Previous)
{
    public GpuPosePlayback Playback { get; init; }
}

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
            readback.Buffer = _backend.CreateBuffer(_device, new BufferDesc(bytes * 2 + 64, BufferUsages.CopyDestination), MemoryType.Readback);
            GraphBufferId output = frame.Import(readback.Buffer,
                [new(BufferRange.Whole, readback.Buffer.InitialSync, readback.Buffer.InitialAccess, ResourceContentState.Undefined)]);
            var data = new ReadbackCopy(_currentBones, _previousBones, _playbackInput, output,
                new BufferRange((ulong)readback.Section.Range.Start * 64, bytes),
                new BufferRange((ulong)readback.Section.PlaybackIndex * 64, 64));
            readback.Pass = frame.AddCopyPass("Read model pose section", PassQueueSelection.AnyOfType(QueueType.Graphics), data,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref ReadbackCopy data) =>
                {
                    pass.Read(data.Current, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Previous, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Playback, data.PlaybackRange, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in ReadbackCopy data) =>
                {
                    BufferRegion current = commands.GetBufferRegion(data.Current), previous = commands.GetBufferRegion(data.Previous);
                    SomeEngine.Graphics.Buffer output = commands.GetBuffer(data.Output);
                    commands.CopyBuffer(new(current.Buffer, current.Range.Offset + data.Range.Offset, output, 0, data.Range.Size));
                    commands.CopyBuffer(new(previous.Buffer, previous.Range.Offset + data.Range.Offset, output, data.Range.Size, data.Range.Size));
                    BufferRegion playback = commands.GetBufferRegion(data.Playback);
                    commands.CopyBuffer(new(playback.Buffer, playback.Range.Offset + data.PlaybackRange.Offset,
                        output, data.Range.Size * 2, 64));
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
            int count = readback.Section.Range.Count;
            ReadOnlySpan<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes[..(count * 128)]);
            PlaybackData playback = MemoryMarshal.Read<PlaybackData>(mapping.Bytes[(count * 128)..]);
            var result = new GpuPoseReadback(playback.Starts, playback.InputNumber, playback.CalculationTime,
                playback.SampleTime, matrices[..count].ToArray(), matrices[count..].ToArray())
            {
                Playback = new(unchecked((int)playback.State), unchecked((int)playback.SourceState), playback.Time,
                    playback.SourceTime, playback.SourceSampleTime, playback.Weight, playback.Finished != 0),
            };
            _readbacks.RemoveAt(index);
            readback.Receive(result);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PlaybackData(uint State, uint SourceState, uint Starts, uint TransitionIndex,
        ulong InputNumber, double CalculationTime, float Time, float SourceTime, float PlaybackTime, float TransitionTime,
        float SampleTime, float SourceSampleTime, float Weight, uint Finished);

    private readonly record struct ReadbackCopy(GraphBufferInput Current, GraphBufferInput Previous,
        GraphBufferInput Playback, GraphBufferId Output, BufferRange Range, BufferRange PlaybackRange);
    private sealed class Readback(RenderPoseSection section, Action<GpuPoseReadback> receive)
    {
        internal readonly RenderPoseSection Section = section;
        internal readonly Action<GpuPoseReadback> Receive = receive;
        internal SomeEngine.Graphics.Buffer? Buffer;
        internal GraphPassId Pass;
        internal QueueCompletion Completion;
    }
}
