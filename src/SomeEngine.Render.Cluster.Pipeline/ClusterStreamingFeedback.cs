using SomeEngine.Render.Cluster;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Completed page-fault data awaiting streaming consumption.</summary>
public sealed class ClusterStreamingFeedbackChannel
{
    private static readonly byte[][] EmptyPayloads = [[]];
    private readonly Queue<ClusterStreamingFeedbackPacket> _packets = new();
    private ClusterStreamingFeedbackStatistics _statistics;

    public ClusterStreamingFeedbackStatistics CaptureStatistics() => _statistics;

    internal void Publish(ulong epoch, ReadOnlySpan<byte> payload)
    {
        var ticket = new ClusterReadbackTicket(new ClusterEpochId(epoch), checked(_statistics.ReceivedFrames + 1));
        uint reportedCount = MemoryMarshal.Read<uint>(payload);
        int storedCount = checked((int)Math.Min(reportedCount, (uint)((payload.Length - sizeof(uint)) / 16)));
        byte[] bytes = reportedCount == 0 ? [] : payload[..(sizeof(uint) + storedCount * 16)].ToArray();
        _packets.Enqueue(new ClusterStreamingFeedbackPacket(ticket, bytes.Length == 0 ? EmptyPayloads : [bytes]));
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
/// Consumes geometry page faults and advances reads through the shared feedback channel.
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
        ConsumeFaults();
        _resources.PumpStreaming();
    }

    private void ConsumeFaults()
    {
        while (_channel.TryTake(out ClusterStreamingFeedbackPacket packet))
        {
            _resources.IngestPageFaultReadbackBatch(packet.Ticket, packet.Payloads);
            _channel.RecordConsumption(packet);
        }
    }
}

internal readonly record struct ClusterStreamingFeedbackPacket(
    ClusterReadbackTicket Ticket,
    byte[][] Payloads);
