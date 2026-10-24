using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private readonly List<Readback> _readbacks = [];

    /// <summary>Requests the next submitted current/previous pair for an extracted model section.</summary>
    public void RequestReadback(in RenderPoseSection section, Action<GpuPoseReadback> receive) =>
        _readbacks.Add(new(section, new(0, section.Range.Count), receive));

    private void RequestReadbacks()
    {
        foreach (Section section in _sectionOrder)
        foreach (GpuPoseReadbackRequest request in section.Data.Readbacks.Span)
        {
            if (request.Completion.IsCompleted || request.Identity != section.Data.Identity
                || _readbacks.Exists(readback => ReferenceEquals(readback.Request, request))) continue;
            var output = new RenderPoseSection(section.Range, section.Data.Starts, section.Data.InputNumber,
                section.FrameTime, section.Data.SampleTime, section.Index, section.FrameVersion);
            _readbacks.Add(new(output, request.Range, request.Deliver) { Request = request });
        }
    }

    private void RecordReadbacks(ref RenderGraphFrame frame)
    {
        foreach (Readback readback in _readbacks)
        {
            if (readback.Buffer is not null) continue;
            Section section = _sectionOrder[readback.Section.PlaybackIndex];
            readback.Identity = section.Data.Identity;
            readback.Presentation = section.Presentation;
            readback.SampledBones = section.SampledBones.Where(bone => bone >= readback.Range.Start
                && bone < readback.Range.Start + readback.Range.Count).ToArray();
            readback.Calculations = new PoseCalculation[readback.Range.Count];
            if (!section.Data.IsGpu) section.Data.Calculations.AsSpan(readback.Range.Start, readback.Range.Count).CopyTo(readback.Calculations);
            else foreach (short bone in section.Data.CpuBones.Span)
                if (bone >= readback.Range.Start && bone < readback.Range.Start + readback.Range.Count)
                    readback.Calculations[bone - readback.Range.Start] = section.Data.Calculations[bone];
            ulong bytes = (ulong)readback.Range.Count * 64;
            readback.Buffer = _backend.CreateBuffer(_device, new BufferDesc(bytes * 4 + 64, BufferUsages.CopyDestination), MemoryType.Readback);
            GraphBufferId output = frame.Import(readback.Buffer,
                [new(BufferRange.Whole, readback.Buffer.InitialSync, readback.Buffer.InitialAccess, ResourceContentState.Undefined)]);
            var data = new ReadbackCopy(_currentBones, _previousBones, _historyCurrentInput, _historyPreviousInput, _playbackInput, output,
                new BufferRange((ulong)(readback.Section.Range.Start + readback.Range.Start) * 64, bytes),
                new BufferRange((ulong)(_boneCount + readback.Section.Range.Start + readback.Range.Start) * 64, bytes),
                new BufferRange((ulong)readback.Section.PlaybackIndex * 64, 64));
            readback.Pass = frame.AddCopyPass("Read model pose section", PassQueueSelection.AnyOfType(QueueType.Graphics), data,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref ReadbackCopy data) =>
                {
                    pass.Read(data.Current, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Previous, data.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Playback, data.PlaybackRange, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.LocalCurrent, data.LocalRange, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.LocalPrevious, data.LocalRange, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in ReadbackCopy data) =>
                {
                    BufferRegion current = commands.GetBufferRegion(data.Current), previous = commands.GetBufferRegion(data.Previous);
                    SomeEngine.Graphics.Buffer output = commands.GetBuffer(data.Output);
                    commands.CopyBuffer(new(current.Buffer, current.Range.Offset + data.Range.Offset, output, 0, data.Range.Size));
                    commands.CopyBuffer(new(previous.Buffer, previous.Range.Offset + data.Range.Offset, output, data.Range.Size, data.Range.Size));
                    BufferRegion localCurrent = commands.GetBufferRegion(data.LocalCurrent), localPrevious = commands.GetBufferRegion(data.LocalPrevious);
                    commands.CopyBuffer(new(localCurrent.Buffer, localCurrent.Range.Offset + data.LocalRange.Offset, output, data.Range.Size * 2, data.Range.Size));
                    commands.CopyBuffer(new(localPrevious.Buffer, localPrevious.Range.Offset + data.LocalRange.Offset, output, data.Range.Size * 3, data.Range.Size));
                    BufferRegion playback = commands.GetBufferRegion(data.Playback);
                    commands.CopyBuffer(new(playback.Buffer, playback.Range.Offset + data.PlaybackRange.Offset,
                        output, data.Range.Size * 4, 64));
                });
            frame.OrderAfter(readback.Pass, CompletePass);
        }
    }

    private void AcceptReadbacks(RenderGraphFrameResult result)
    {
        foreach (Readback readback in _readbacks)
            if (result.TryGetPassCompletion(readback.Pass, out QueueCompletion completion)) readback.Completion = completion;
    }

    /// <summary>Delivers completed GPU copies as independently retained matrices.</summary>
    public void CompleteReadbacks() => CompleteReadbacks(wait: false);

    private void CompleteReadbacks(bool wait)
    {
        for (int index = 0; index < _readbacks.Count;)
        {
            Readback readback = _readbacks[index];
            if (readback.Completion == default) { index++; continue; }
            if (!_backend.IsComplete(readback.Completion))
            {
                if (!wait) { index++; continue; }
                if (_backend.WaitCpu(readback.Completion, TimeSpan.FromSeconds(60)) != WaitStatus.Completed)
                    throw new TimeoutException("Model pose readback did not complete.");
            }
            using SomeEngine.Graphics.Buffer buffer = readback.Buffer!;
            using MappedBuffer mapping = _backend.Map(buffer, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            int count = readback.Range.Count;
            ReadOnlySpan<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes[..(count * 256)]);
            PlaybackData playback = MemoryMarshal.Read<PlaybackData>(mapping.Bytes[(count * 256)..]);
            var sourceSamples = new GpuPoseSampleTimes[count];
            for (int bone = 0; bone < count; bone++)
            {
                sourceSamples[bone] = new((float)readback.Calculations[bone].SampleTime, (float)readback.Calculations[bone].SampleTime);
                if (readback.Calculations[bone].Version == 0)
                {
                    double sampleTime = 0;
                    if (readback.SampledBones.AsSpan().BinarySearch((short)(readback.Range.Start + bone)) >= 0)
                    {
                        float current = matrices[count * 2 + bone].M41, previous = matrices[count * 3 + bone].M41;
                        sourceSamples[bone] = new(previous, current);
                        sampleTime = previous + (current - previous) * readback.Presentation.Amount;
                    }
                    readback.Calculations[bone] = new(readback.Section.Version, readback.Section.CalculationTime, sampleTime);
                }
            }
            var result = new GpuPoseReadback(playback.Starts, playback.InputNumber, readback.Section.CalculationTime,
                playback.SampleTime, matrices[..count].ToArray(), matrices.Slice(count, count).ToArray())
            {
                Identity = readback.Identity with { Starts = playback.Starts, InputNumber = playback.InputNumber },
                Range = readback.Range,
                Presentation = readback.Presentation,
                SampledBones = readback.SampledBones,
                SourceSamples = sourceSamples,
                Version = readback.Section.Version,
                SourceCalculationTime = playback.CalculationTime,
                Calculations = readback.Calculations,
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
        GraphBufferInput LocalCurrent, GraphBufferInput LocalPrevious, GraphBufferInput Playback, GraphBufferId Output,
        BufferRange Range, BufferRange LocalRange, BufferRange PlaybackRange);
    private sealed class Readback(RenderPoseSection section, PoseRange range, Action<GpuPoseReadback> receive)
    {
        internal readonly RenderPoseSection Section = section;
        internal readonly PoseRange Range = range;
        internal readonly Action<GpuPoseReadback> Receive = receive;
        internal GpuPoseReadbackRequest? Request;
        internal GpuPoseIdentity Identity;
        internal GpuPosePresentation Presentation;
        internal short[] SampledBones = [];
        internal SomeEngine.Graphics.Buffer? Buffer;
        internal GraphPassId Pass;
        internal QueueCompletion Completion;
        internal PoseCalculation[] Calculations = [];
    }
}
