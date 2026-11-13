using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private readonly List<Readback> _readbacks = [];
    public RenderFeedback<GpuPoseReadback> PoseFeedback { get; } = new();

    public void RequestReadback(in RenderPoseSection section, Action<GpuPoseReadbackResult> receive) =>
        _readbacks.Add(new(section, new(0, section.Range.Count), receive));

    private void RequestReadbacks()
    {
        _readbacks.RemoveAll(readback => readback.Request is { } request &&
            !_sectionOrder.Any(section => section.Data.Identity == request.Identity));
        foreach (Section section in _sectionOrder)
        foreach (GpuPoseReadbackRequest request in section.Data.Readbacks.Span)
        {
            if (request.Identity != section.Data.Identity
                || _readbacks.Exists(readback => readback.Request == request)) continue;
            var output = new RenderPoseSection(section.Range, section.Data.Starts, section.Data.InputNumber,
                section.FrameTime, section.Data.SampleTime, section.Index, section.FrameVersion);
            _readbacks.Add(new(output, request.Range, null) { Request = request });
        }
    }

    private void RecordReadbacks(ref RenderGraphFrame frame)
    {
        Readback[] requests = _readbacks.ToArray();
        foreach (Readback readback in requests)
        {
            Section section = _sectionOrder[readback.Section.PlaybackIndex];
            var outputSection = new RenderPoseSection(section.Range, section.Data.Starts, section.Data.InputNumber,
                section.FrameTime, section.Data.SampleTime, section.Index, section.FrameVersion);
            short[] sampledBones = section.SampledBones.Where(bone => bone >= readback.Range.Start
                && bone < readback.Range.Start + readback.Range.Count).ToArray();
            var calculations = new PoseCalculation[readback.Range.Count];
            if (!section.Data.IsGpu) section.Data.Calculations.AsSpan(readback.Range.Start, readback.Range.Count).CopyTo(calculations);
            else foreach (short bone in section.Data.CpuBones.Span)
                if (bone >= readback.Range.Start && bone < readback.Range.Start + readback.Range.Count)
                    calculations[bone - readback.Range.Start] = section.Data.Calculations[bone];
            ulong bytes = (ulong)readback.Range.Count * 64;
            GraphBufferId output = frame.CreateBuffer(new BufferDesc(bytes * 4 + 64,
                BufferUsages.CopyDestination | BufferUsages.CopySource, "Pose readback bytes"));
            var data = new ReadbackCopy(_currentBones, _previousBones, _historyCurrentInput, _historyPreviousInput, _playbackInput, output,
                new BufferRange((ulong)(section.Range.Start + readback.Range.Start) * 64, bytes),
                new BufferRange((ulong)(_boneCount + section.Range.Start + readback.Range.Start) * 64, bytes),
                new BufferRange((ulong)section.Index * 64, 64));
            GraphPassId copy = frame.AddCopyPass("Read model pose section", PassQueueSelection.AnyOfType(QueueType.Graphics), data,
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
            frame.OrderAfter(copy, CompletePass);
            GraphReadbackId delivery = frame.AddReadback(new(output, new(0, bytes * 4 + 64)),
                new PoseReadbackBytes(PoseFeedback, readback, outputSection, section.Data.Identity, section.Presentation,
                    sampledBones, calculations), "Read model pose section bytes", frame.FrameIdentity);
            frame.OrderAfter(delivery, copy);
        }
        if (requests.Length != 0) frame.ObserveExecution(new ReadbackSubmission(this, requests));
    }

    private sealed class ReadbackSubmission(SkinningFeature feature, Readback[] requests) : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            foreach (Readback request in requests) feature._readbacks.Remove(request);
        }
    }

    public void CompleteReadbacks() => _graph.CollectCompleted();

    private void CompleteReadbacks(bool wait)
    {
        if (wait && _graph.WaitForFrame(_graph.LastSubmittedFrame, TimeSpan.FromSeconds(60)) != WaitStatus.Completed)
            throw new TimeoutException("Model pose readback did not complete.");
        _graph.CollectCompleted();
    }

    private sealed class PoseReadbackBytes(RenderFeedback<GpuPoseReadback> feedback, Readback readback, RenderPoseSection section,
        GpuPoseIdentity identity, GpuPosePresentation presentation, short[] sampledBones,
        PoseCalculation[] calculations) : IGraphReadbackReceiver
    {
        private readonly RenderPoseSection _section = section;
        private readonly GpuPoseIdentity _identity = identity;
        private readonly GpuPosePresentation _presentation = presentation;
        private readonly short[] _sampledBones = sampledBones;
        private readonly PoseCalculation[] _calculations = calculations;

        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            int count = readback.Range.Count;
            ReadOnlySpan<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(bytes[..(count * 256)]);
            PlaybackData playback = MemoryMarshal.Read<PlaybackData>(bytes[(count * 256)..]);
            var sourceSamples = new GpuPoseSampleTimes[count];
            for (int bone = 0; bone < count; bone++)
            {
                sourceSamples[bone] = new((float)_calculations[bone].SampleTime, (float)_calculations[bone].SampleTime);
                if (_calculations[bone].Version == 0)
                {
                    double sampleTime = 0;
                    if (_sampledBones.AsSpan().BinarySearch((short)(readback.Range.Start + bone)) >= 0)
                    {
                        float current = matrices[count * 2 + bone].M41, previous = matrices[count * 3 + bone].M41;
                        sourceSamples[bone] = new(previous, current);
                        sampleTime = previous + (current - previous) * _presentation.Amount;
                    }
                    _calculations[bone] = new(_section.Version, _section.CalculationTime, sampleTime);
                }
            }
            var result = new GpuPoseReadbackResult(playback.Starts, playback.InputNumber, _section.CalculationTime,
                playback.SampleTime, matrices[..count].ToArray(), matrices.Slice(count, count).ToArray())
            {
                Identity = _identity with { Starts = playback.Starts, InputNumber = playback.InputNumber },
                Range = readback.Range,
                Presentation = _presentation,
                SampledBones = _sampledBones,
                SourceSamples = sourceSamples,
                Version = _section.Version,
                SourceCalculationTime = playback.CalculationTime,
                Calculations = _calculations,
                Playback = new(unchecked((int)playback.State), unchecked((int)playback.SourceState), playback.Time,
                    playback.SourceTime, playback.SourceSampleTime, playback.Weight, playback.Finished != 0),
            };
            if (readback.Request is not null)
                for (int bone = 0; bone < count; bone++)
                {
                    var value = new GpuPoseReadback(result.Identity.Target, result.Starts, result.InputNumber,
                        result.Identity.AnimationStarts, result.Range, bone, result.Current.Span[bone], result.Previous.Span[bone],
                        result.Calculations.Span[bone], result.SampledBones.Span.Contains((short)(readback.Range.Start + bone)),
                        result.SourceSamples.Span[bone], GpuPoseFeedbackPresentation.From(result.Presentation), result.Playback, result.Version,
                        result.CalculationTime, result.SourceCalculationTime, result.SampleTime);
                    feedback.Write(tag, in value);
                }
            else readback.Receive!(result);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PlaybackData(uint State, uint SourceState, uint Starts, uint TransitionIndex,
        ulong InputNumber, double CalculationTime, float Time, float SourceTime, float PlaybackTime, float TransitionTime,
        float SampleTime, float SourceSampleTime, float Weight, uint Finished);

    private readonly record struct ReadbackCopy(GraphBufferInput Current, GraphBufferInput Previous,
        GraphBufferInput LocalCurrent, GraphBufferInput LocalPrevious, GraphBufferInput Playback, GraphBufferId Output,
        BufferRange Range, BufferRange LocalRange, BufferRange PlaybackRange);
    private sealed class Readback(RenderPoseSection section, PoseRange range, Action<GpuPoseReadbackResult>? receive)
    {
        internal readonly RenderPoseSection Section = section;
        internal readonly PoseRange Range = range;
        internal readonly Action<GpuPoseReadbackResult>? Receive = receive;
        internal GpuPoseReadbackRequest? Request;
    }
}
