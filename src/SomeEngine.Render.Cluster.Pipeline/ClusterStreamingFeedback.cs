using SomeEngine.Render.Cluster;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Writes completed geometry observations to the renderer's feedback queue.</summary>
public static class ClusterStreamingFeedback
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuPageDemand(uint PageId, uint Priority);

    internal static void WriteFrame(RenderFeedback<ClusterPageDemand> feedback, ulong epoch, ulong frameIdentity, int readbackCount)
    {
        if (readbackCount == 0) return;
        var value = new ClusterPageDemand { Epoch = epoch, Kind = ClusterPageDemandKind.FrameEnd, Count = (uint)readbackCount };
        feedback.Write(frameIdentity, in value);
    }

    internal static void WriteReadback(RenderFeedback<ClusterPageDemand> feedback, ulong epoch, ulong frameIdentity,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> demandPayload)
    {
        uint reportedFaults = MemoryMarshal.Read<uint>(payload);
        int faultCount = (int)Math.Min(reportedFaults, (uint)((payload.Length - sizeof(uint)) / 16));
        ReadOnlySpan<ClusterPageRequest> faults = MemoryMarshal.Cast<byte, ClusterPageRequest>(payload.Slice(sizeof(uint), faultCount * 16));
        uint reportedDemand = MemoryMarshal.Read<uint>(demandPayload);
        int demandCount = (int)Math.Min(reportedDemand, (uint)((demandPayload.Length - sizeof(uint)) / 8));
        ReadOnlySpan<GpuPageDemand> demands = MemoryMarshal.Cast<byte, GpuPageDemand>(demandPayload.Slice(sizeof(uint), demandCount * 8));
        var values = new ClusterPageDemand[faultCount + demandCount + 1];
        int index = 0;
        foreach (ClusterPageRequest fault in faults)
            values[index++] = new(fault.FirstPageId, fault.Priority)
            { Epoch = epoch, Kind = ClusterPageDemandKind.Fault, LeafNodeIndex = fault.LeafNodeIndex, PageCount = fault.PageCount };
        foreach (GpuPageDemand demand in demands)
            values[index++] = new(demand.PageId, demand.Priority) { Epoch = epoch };
        values[index] = new() { Epoch = epoch, Kind = ClusterPageDemandKind.ViewEnd, Count = reportedFaults,
            Complete = reportedDemand == (uint)demandCount };
        feedback.Write(frameIdentity, values.AsSpan());
    }
}

public readonly record struct ClusterStreamingFeedbackStatistics(
    ulong ReceivedFrames, ulong ConsumedFrames, ulong ReportedPageFaults, ulong ConsumedPageFaults, ulong LastEpoch);

/// <summary>Merges completed view observations and advances the shared geometry page stream.</summary>
public sealed class ClusterStreamingFeedbackConsumer
{
    private readonly ClusterRenderResources _resources;
    private readonly RenderFeedback<ClusterPageDemand> _feedback;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly Dictionary<(ulong Epoch, ulong Frame), FrameFeedback> _frames = [];
    private ClusterStreamingFeedbackStatistics _statistics;

    public ClusterStreamingFeedbackConsumer(ClusterRenderResources resources, RenderFeedback<ClusterPageDemand> feedback,
        global::SomeEngine.RenderGraph.RenderGraph graph) => (_resources, _feedback, _graph) = (resources, feedback, graph);

    public ClusterStreamingFeedbackStatistics CaptureStatistics() => _statistics;

    public void Pump()
    {
        while (_feedback.TryRead(_graph.LastCompletedFrame, out RenderFeedbackRecord<ClusterPageDemand> record))
        {
            ClusterPageDemand value = record.Value;
            if (value.Epoch != _resources.EpochId.Value) continue;
            var key = (value.Epoch, record.FrameIdentity);
            if (!_frames.TryGetValue(key, out FrameFeedback? frame)) _frames.Add(key, frame = new());
            switch (value.Kind)
            {
                case ClusterPageDemandKind.FrameEnd: frame.ExpectedViews = value.Count; break;
                case ClusterPageDemandKind.Fault:
                    frame.ViewFaults.Add(new(value.LeafNodeIndex, value.PageId, value.PageCount, value.Priority));
                    break;
                case ClusterPageDemandKind.Demand:
                    frame.Demands[value.PageId] = Math.Max(frame.Demands.GetValueOrDefault(value.PageId), value.Priority);
                    break;
                case ClusterPageDemandKind.ViewEnd:
                    frame.ReportedFaults += value.Count;
                    frame.Complete &= value.Complete;
                    frame.EndedViews++;
                    if (value.Count != 0)
                    {
                        byte[] bytes = new byte[sizeof(uint) + frame.ViewFaults.Count * 16];
                        uint reported = value.Count;
                        MemoryMarshal.Write(bytes, in reported);
                        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(frame.ViewFaults)).CopyTo(bytes.AsSpan(sizeof(uint)));
                        frame.Payloads.Add(bytes);
                    }
                    frame.ViewFaults.Clear();
                    break;
            }
            if (frame.ExpectedViews == 0 || frame.ExpectedViews != frame.EndedViews) continue;
            var ticket = new ClusterReadbackTicket(new(value.Epoch), _statistics.ReceivedFrames + 1);
            ClusterPagePriority[] demands = frame.Demands.Select(pair => new ClusterPagePriority(pair.Key, pair.Value)).ToArray();
            byte[][] payloads = frame.Payloads.Count == 0 ? [[]] : frame.Payloads.ToArray();
            _resources.IngestStreamingFeedback(ticket, payloads, demands, frame.Complete, record.FrameIdentity);
            _statistics = new(ticket.FeedbackSequence, _statistics.ConsumedFrames + 1,
                _statistics.ReportedPageFaults + frame.ReportedFaults,
                _statistics.ConsumedPageFaults + frame.ReportedFaults, value.Epoch);
            _frames.Remove(key);
        }
        _resources.PumpStreaming();
    }

    private sealed class FrameFeedback
    {
        internal uint ExpectedViews, EndedViews;
        internal ulong ReportedFaults;
        internal bool Complete = true;
        internal readonly List<ClusterPageRequest> ViewFaults = [];
        internal readonly List<byte[]> Payloads = [];
        internal readonly Dictionary<uint, uint> Demands = [];
    }
}
