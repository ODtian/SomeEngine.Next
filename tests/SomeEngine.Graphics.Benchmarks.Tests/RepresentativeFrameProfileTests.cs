using System.Buffers.Binary;

namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class RepresentativeFrameProfileTests
{
    [Fact]
    public void WorkerRangesCoverEachObjectExactlyOnce()
    {
        int cursor = 0;
        for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
        {
            (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
            Assert.Equal(cursor, start);
            Assert.True(count > 0);
            cursor += count;
        }

        Assert.Equal(RepresentativeFrameProfile.ObjectCount, cursor);

    }

    [Fact]
    public void ObjectPacketWriterIsDeterministic()
    {
        byte[] first = new byte[
            RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize];
        byte[] second = new byte[first.Length];

        RepresentativeFrameProfile.WriteObjectPackets(first, 17);
        RepresentativeFrameProfile.WriteObjectPackets(second, 17);

        Assert.Equal(first, second);
        Assert.Contains(first, static value => value != 0);
    }

    [Fact]
    public void ObjectPacketWriterUsesTheNativeIncrementingLayout()
    {
        byte[] packets = new byte[
            RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize];

        RepresentativeFrameProfile.WriteObjectPackets(packets, 17);

        AssertPacket(packets, 0, 0, 17, 17 * 31, 0 ^ 17);
        AssertPacket(packets, 1, 1, 17, 17 * 31 + 17, 1 ^ 17);
        int last = RepresentativeFrameProfile.ObjectCount - 1;
        AssertPacket(
            packets,
            last,
            last,
            17,
            17 * 31 + last * 17,
            last ^ 17);
    }

    [Fact]
    public void CompleteReportsPostCloseCleanupSeparately()
    {
        FrameSample[] samples =
        [
            new(
                0,
                100,
                10,
                null,
                0,
                0,
                1,
                30,
                3),
        ];

        CommandWorkloadEvidence workloadEvidence =
            RepresentativeFrameProfile.CreateWorkloadEvidence();
        WorkloadRun run = BenchmarkOutput.Complete(
            GraphicsWorkload.RepresentativeFrameSerial,
            BenchmarkProfile.RepresentativeCpuFrame,
            0,
            1,
            RepresentativeFrameProfile.DrawCount,
            RepresentativeFrameProfile.BarrierCount,
            samples,
            [],
            "output",
            "shader",
            [],
            workloadEvidence);

        Assert.Equal(10, run.Cpu!.Value.P50);
        Assert.Equal(3, run.PostCloseCleanup!.Value.P50);
        Assert.Equal(workloadEvidence, run.WorkloadEvidence);
        Assert.Equal([3], WorkloadMeasurements.Create(run).PostCloseCleanupSamples);
    }


    private static void AssertPacket(
        byte[] packets,
        int packetIndex,
        int objectIndex,
        int frameIndex,
        int mixed,
        int xor)
    {
        ReadOnlySpan<byte> packet = packets.AsSpan(
            packetIndex * RepresentativeFrameProfile.ObjectPacketSize,
            RepresentativeFrameProfile.ObjectPacketSize);
        Assert.Equal(objectIndex, BinaryPrimitives.ReadInt32LittleEndian(packet));
        Assert.Equal(frameIndex, BinaryPrimitives.ReadInt32LittleEndian(packet[4..]));
        Assert.Equal(mixed, BinaryPrimitives.ReadInt32LittleEndian(packet[8..]));
        Assert.Equal(xor, BinaryPrimitives.ReadInt32LittleEndian(packet[12..]));
    }
}
