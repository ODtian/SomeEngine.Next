using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Fixed-capacity publication boundary between geometry fault readback and streaming policy. The
/// geometry producer never resolves pages or advances streaming work.
/// </summary>
public sealed class ClusterStreamingFeedbackChannel
{
    private const int Capacity = 2;
    private readonly ClusterStreamingFeedbackPacket[] _packets =
        new ClusterStreamingFeedbackPacket[Capacity];
    private int _readIndex;
    private int _count;

    internal void Publish(ClusterReadbackTicket ticket, ReadOnlySpan<byte> payload)
    {
        if (!ticket.IsValid)
            throw new ArgumentException("Cluster streaming feedback requires a valid ticket.", nameof(ticket));
        if (_count == Capacity)
        {
            throw new InvalidOperationException(
                "Cluster streaming feedback was not consumed before its fixed publication capacity filled.");
        }
        int writeIndex = (_readIndex + _count) % Capacity;
        _packets[writeIndex] = new ClusterStreamingFeedbackPacket(ticket, payload.ToArray());
        _count++;
    }

    internal bool TryTake(out ClusterStreamingFeedbackPacket packet)
    {
        if (_count == 0)
        {
            packet = default;
            return false;
        }
        packet = _packets[_readIndex];
        _packets[_readIndex] = default;
        _readIndex = (_readIndex + 1) % Capacity;
        _count--;
        return true;
    }
}

/// <summary>
/// Independently consumes authenticated geometry-fault publications and owns streaming progress.
/// Disabling or replacing this consumer does not alter the Cluster geometry or overflow ABI.
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
        while (_channel.TryTake(out ClusterStreamingFeedbackPacket packet))
            _resources.IngestPageFaultReadback(packet.Ticket, packet.Payload);
        _resources.PumpStreaming();
    }
}

internal readonly record struct ClusterStreamingFeedbackPacket(
    ClusterReadbackTicket Ticket,
    byte[] Payload);
