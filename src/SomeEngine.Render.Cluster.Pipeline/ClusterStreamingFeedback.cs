using SomeEngine.Render.Cluster;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Completed page faults and visible demand awaiting streaming consumption.</summary>
public sealed class ClusterStreamingFeedbackChannel
{
    private static readonly byte[][] EmptyPayloads = [[]];
    private readonly Queue<ClusterStreamingFeedbackPacket> _packets = new();
    private ClusterStreamingFeedbackStatistics _statistics;
    private readonly Dictionary<(ulong Epoch, ulong Frame), ViewFeedback> _viewFeedback = [];

    private sealed class ViewFeedback(int count)
    {
        internal int Remaining = count;
        internal ulong ReportedFaults;
        internal bool Complete = true;
        internal readonly List<byte[]> Payloads = [];
        internal readonly Dictionary<uint, uint> Demands = [];
    }

    // Readbacks are counted only after RenderGraph has queued their completed-byte delivery.
    // A single view follows the original path without a per-frame aggregation object.
    internal void ExpectFrame(ulong epoch, ulong frameIdentity, int readbackCount)
    {
        if (readbackCount > 1) _viewFeedback.Add((epoch, frameIdentity), new ViewFeedback(readbackCount));
    }

    public ClusterStreamingFeedbackStatistics CaptureStatistics() => _statistics;

    internal void Publish(ulong epoch, ulong frameIdentity, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> demandPayload)
    {
        uint reportedCount = MemoryMarshal.Read<uint>(payload);
        int storedCount = checked((int)Math.Min(reportedCount, (uint)((payload.Length - sizeof(uint)) / 16)));
        byte[] bytes = reportedCount == 0 ? [] : payload[..(sizeof(uint) + storedCount * 16)].ToArray();
        uint reportedDemand = MemoryMarshal.Read<uint>(demandPayload);
        int storedDemand = checked((int)Math.Min(reportedDemand, (uint)((demandPayload.Length - sizeof(uint)) / 8)));
        ClusterPageDemand[] demands = MemoryMarshal.Cast<byte, ClusterPageDemand>(
            demandPayload.Slice(sizeof(uint), storedDemand * 8)).ToArray();
        if (_viewFeedback.TryGetValue((epoch, frameIdentity), out ViewFeedback? views))
        {
            if (bytes.Length != 0) views.Payloads.Add(bytes);
            views.ReportedFaults += reportedCount;
            views.Complete &= reportedDemand == (uint)storedDemand;
            foreach (ClusterPageDemand demand in demands)
                views.Demands[demand.PageId] = Math.Max(views.Demands.GetValueOrDefault(demand.PageId), demand.Priority);
            if (--views.Remaining != 0) return;
            _viewFeedback.Remove((epoch, frameIdentity));
            Enqueue(epoch, frameIdentity, views.Payloads.Count == 0 ? EmptyPayloads : views.Payloads.ToArray(),
                views.Demands.Select(static pair => new ClusterPageDemand(pair.Key, pair.Value)).ToArray(),
                views.Complete, views.ReportedFaults);
        }
        else
        {
            Enqueue(epoch, frameIdentity, bytes.Length == 0 ? EmptyPayloads : [bytes], demands,
                reportedDemand == (uint)storedDemand, reportedCount);
        }
    }

    private void Enqueue(ulong epoch, ulong frameIdentity, byte[][] payloads, ClusterPageDemand[] demands,
        bool complete, ulong reportedCount)
    {
        var ticket = new ClusterReadbackTicket(new ClusterEpochId(epoch), checked(_statistics.ReceivedFrames + 1));
        _packets.Enqueue(new ClusterStreamingFeedbackPacket(ticket, payloads, demands, complete, frameIdentity));
        _statistics = _statistics with
        {
            ReceivedFrames = ticket.FeedbackSequence,
            ReportedPageFaults = _statistics.ReportedPageFaults + reportedCount,
            LastEpoch = ticket.EpochId.Value,
        };
    }

    internal bool TryTake(out ClusterStreamingFeedbackPacket packet) => _packets.TryDequeue(out packet);

    internal void RecordConsumption(ClusterStreamingFeedbackPacket packet)
    {
        ulong count = 0;
        foreach (byte[] payload in packet.Payloads)
            if (payload.Length != 0) count += MemoryMarshal.Read<uint>(payload);
        _statistics = _statistics with
        {
            ConsumedFrames = _statistics.ConsumedFrames + 1,
            ConsumedPageFaults = _statistics.ConsumedPageFaults + count,
        };
    }
}

public readonly record struct ClusterStreamingFeedbackStatistics(
    ulong ReceivedFrames, ulong ConsumedFrames, ulong ReportedPageFaults, ulong ConsumedPageFaults, ulong LastEpoch);

/// <summary>
/// Consumes geometry demand and advances reads through the shared feedback channel.
/// </summary>
public sealed class ClusterStreamingFeedbackConsumer
{
    private readonly ClusterRenderResources _resources;
    private readonly ClusterStreamingFeedbackChannel _channel;

    public ClusterStreamingFeedbackConsumer(
        ClusterRenderResources resources,
        ClusterStreamingFeedbackChannel channel)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public void Pump()
    {
        ConsumeFeedback();
        _resources.PumpStreaming();
    }

    private void ConsumeFeedback()
    {
        while (_channel.TryTake(out ClusterStreamingFeedbackPacket packet))
        {
            _resources.IngestStreamingFeedback(packet.Ticket, packet.Payloads, packet.Demands, packet.DemandComplete, packet.FrameIdentity);
            _channel.RecordConsumption(packet);
        }
    }
}

internal readonly record struct ClusterStreamingFeedbackPacket(
    ClusterReadbackTicket Ticket,
    byte[][] Payloads,
    ClusterPageDemand[] Demands,
    bool DemandComplete,
    ulong FrameIdentity);
