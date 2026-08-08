using System.Runtime.InteropServices;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class PageFaultsTests
{
    private const int FaultCapacity = 4096;
    private static readonly ClusterEpochId Epoch = new(42);

    [Fact]
    public void ReadDropsCountWordAndPreservesDuplicatePages()
    {
        uint[] words = [3, 11, 17, 11];
        var pageFaults = new PageFaults(Epoch, FaultCapacity);

        PageFaultRead read = pageFaults.Read(MemoryMarshal.AsBytes(words.AsSpan()));

        Assert.Equal(Epoch, read.EpochId);
        Assert.Equal(3u, read.ReportedCount);
        Assert.Equal(3u, read.StoredCount);
        Assert.Equal(0u, read.DroppedCount);
        Assert.False(read.WasTruncated);
        Assert.True(read.LeafNodeIndices.SequenceEqual([11u, 17u, 11u]));
    }

    [Fact]
    public void ReadClampsToAvailableWordsAndConfiguredCapacity()
    {
        uint[] words = [5, 1, 2, 3];
        var pageFaults = new PageFaults(Epoch, capacity: 2);

        PageFaultRead read = pageFaults.Read(MemoryMarshal.AsBytes(words.AsSpan()));

        Assert.Equal(5u, read.ReportedCount);
        Assert.Equal(2u, read.StoredCount);
        Assert.Equal(3u, read.DroppedCount);
        Assert.True(read.WasTruncated);
        Assert.True(read.LeafNodeIndices.SequenceEqual([1u, 2u]));
    }

    [Fact]
    public void ReadNeverExceedsTheConfiguredGpuQueueCapacity()
    {
        uint[] words = new uint[FaultCapacity + 2];
        words[0] = FaultCapacity + 10;
        var pageFaults = new PageFaults(Epoch, FaultCapacity);

        PageFaultRead read = pageFaults.Read(MemoryMarshal.AsBytes(words.AsSpan()));

        Assert.Equal(FaultCapacity + 10u, read.ReportedCount);
        Assert.Equal((uint)FaultCapacity, read.StoredCount);
        Assert.Equal(10u, read.DroppedCount);
    }

    [Fact]
    public void ReadRejectsPartialWords()
    {
        var pageFaults = new PageFaults(Epoch, FaultCapacity);

        Assert.Throws<InvalidDataException>(() => pageFaults.Read(new byte[sizeof(uint) + 1]));
    }
}
