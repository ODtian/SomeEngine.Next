using SomeEngine.Animation.Pose;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private readonly List<(GraphPassId Pass, DevicePoseOutputRead Read)> _devicePoseReads = [];

    private void CopyDeviceOutputs(ref RenderGraphFrame frame)
    {
        var sections = _sectionOrder.Where(section => section.Data.DeviceOutput is { CopyRequired: true }).ToArray();
        if (sections.Length == 0) return;
        var boundaries = new Dictionary<Buffer, List<BufferBoundaryState>>();
        foreach (Section section in sections)
        {
            DevicePoseOutput output = section.Data.DeviceOutput!.Output;
            if (!boundaries.TryGetValue(output.Buffer, out var ranges)) boundaries.Add(output.Buffer, ranges = []);
            foreach (BufferRange range in new[] { output.Models, output.CurrentSamples, output.PreviousSamples, output.Playback })
                ranges.Add(new(range, PipelineSync.Copy, ResourceAccess.CopySource, ResourceContentState.Defined));
        }
        var buffers = new Dictionary<Buffer, GraphBufferId>();
        foreach (var entry in boundaries) buffers.Add(entry.Key, frame.Import(entry.Key, entry.Value.ToArray()));
        var regions = new DevicePoseCopy[sections.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            Section section = sections[index];
            DevicePoseOutput output = section.Data.DeviceOutput!.Output;
            regions[index] = new(buffers[output.Buffer], output.Models, output.CurrentSamples, output.PreviousSamples, output.Playback,
                new((ulong)section.Range.Start * 64, (ulong)section.Range.Count * 64),
                new((ulong)(_boneCount + section.Range.Start) * 64, (ulong)section.Range.Count * 64),
                new((ulong)section.Index * 64, 64));
        }
        var copies = new DevicePoseCopies(_currentBones, _historyCurrentInput, _historyPreviousInput, _playbackInput, regions);
        GraphPassId pass = frame.AddCopyPass("Copy completed Game poses", PassQueueSelection.AnyOfType(QueueType.Graphics),
            copies, new PassOptions(Culling: PassCullingMode.NeverCull), DevicePoseCopies.Declare, DevicePoseCopies.Record);
        frame.OrderAfter(pass, _advancePass);
        frame.OrderAfter(_reframePass, pass);
        foreach (Section section in sections) _devicePoseReads.Add((pass, section.Data.DeviceOutput!));
    }

    private void FinishDeviceReads(RenderGraphFrameResult result)
    {
        foreach (var read in _devicePoseReads)
        {
            if (result.TryGetPassCompletion(read.Pass, out QueueCompletion completion))
                read.Read.CompleteAfter(_backend, _device, completion);
            else read.Read.Dispose();
        }
        _devicePoseReads.Clear();
    }

    private readonly record struct DevicePoseCopy(GraphBufferId Source, BufferRange Models, BufferRange CurrentSamples,
        BufferRange PreviousSamples, BufferRange Playback, BufferRange DestinationModels, BufferRange DestinationSamples,
        BufferRange DestinationPlayback);
    private readonly record struct DevicePoseCopies(GraphBufferInput Models, GraphBufferInput CurrentSamples,
        GraphBufferInput PreviousSamples, GraphBufferInput Playback, DevicePoseCopy[] Regions)
    {
        internal static void Declare(ref PassDefinition pass, ref DevicePoseCopies data)
        {
            foreach (DevicePoseCopy region in data.Regions)
            {
                foreach (BufferRange range in new[] { region.Models, region.CurrentSamples, region.PreviousSamples, region.Playback })
                    pass.Read(region.Source, range, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Models, region.DestinationModels, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                pass.Write(data.CurrentSamples, region.DestinationSamples, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                pass.Write(data.PreviousSamples, region.DestinationSamples, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                pass.Write(data.Playback, region.DestinationPlayback, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }
        }
        internal static void Record(ref CopyPassCommandScope commands, in DevicePoseCopies data)
        {
            BufferRegion models = commands.GetBufferRegion(data.Models), current = commands.GetBufferRegion(data.CurrentSamples),
                previous = commands.GetBufferRegion(data.PreviousSamples), playback = commands.GetBufferRegion(data.Playback);
            foreach (DevicePoseCopy region in data.Regions)
            {
                Buffer source = commands.GetBuffer(region.Source);
                commands.CopyBuffer(new(source, region.Models.Offset, models.Buffer,
                    models.Range.Offset + region.DestinationModels.Offset, region.Models.Size));
                commands.CopyBuffer(new(source, region.CurrentSamples.Offset, current.Buffer,
                    current.Range.Offset + region.DestinationSamples.Offset, region.CurrentSamples.Size));
                commands.CopyBuffer(new(source, region.PreviousSamples.Offset, previous.Buffer,
                    previous.Range.Offset + region.DestinationSamples.Offset, region.PreviousSamples.Size));
                commands.CopyBuffer(new(source, region.Playback.Offset, playback.Buffer,
                    playback.Range.Offset + region.DestinationPlayback.Offset, region.Playback.Size));
            }
        }
    }
}
