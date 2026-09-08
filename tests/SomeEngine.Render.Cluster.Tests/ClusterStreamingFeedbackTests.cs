using System.Buffers.Binary;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterStreamingFeedbackTests
{
    [Fact]
    public void Equal_tickets_coalesce_without_combining_producer_payloads()
    {
        var channel = new ClusterStreamingFeedbackChannel();
        var ticket = new ClusterReadbackTicket(new ClusterEpochId(7), 12);
        byte[] receiver = FaultPayload(2, 101, 202);
        byte[] shadow = FaultPayload(3, 303, 404, 505);

        channel.Publish(ticket, receiver);
        channel.Publish(ticket, shadow);

        Assert.True(channel.TryTake(out ClusterStreamingFeedbackPacket packet));
        Assert.Equal(ticket, packet.Ticket);
        Assert.Equal(2, packet.Payloads.Length);
        Assert.Equal(receiver, packet.Payloads[0]);
        Assert.Equal(shadow, packet.Payloads[1]);
        Assert.False(channel.TryTake(out _));
    }

    [Fact]
    public void Distinct_tickets_still_consume_fixed_frame_capacity()
    {
        var channel = new ClusterStreamingFeedbackChannel();
        ClusterEpochId epoch = new(9);

        channel.Publish(new ClusterReadbackTicket(epoch, 1), FaultPayload(0));
        channel.Publish(new ClusterReadbackTicket(epoch, 2), FaultPayload(0));

        Assert.Throws<InvalidOperationException>(() =>
            channel.Publish(new ClusterReadbackTicket(epoch, 3), FaultPayload(0)));
    }

    private static byte[] FaultPayload(uint reportedCount, params uint[] leafNodeIndices)
    {
        byte[] payload = new byte[checked((leafNodeIndices.Length + 1) * sizeof(uint))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, reportedCount);
        for (int index = 0; index < leafNodeIndices.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(checked((index + 1) * sizeof(uint))),
                leafNodeIndices[index]);
        }
        return payload;
    }
}
