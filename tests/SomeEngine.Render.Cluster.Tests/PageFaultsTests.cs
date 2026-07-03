using System.Runtime.InteropServices;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class PageFaultsTests
{
    [Fact]
    public void ReadDropsCountWordAndPreservesDuplicateNodes()
    {
        uint[] words = [3, 11, 17, 11];
        var pageFaults = new PageFaults();

        ReadOnlySpan<uint> nodes = pageFaults.Read(
            MemoryMarshal.AsBytes(words.AsSpan()),
            PageFaults.MaxCount);

        Assert.Equal([11u, 17u, 11u], nodes.ToArray());
    }

    [Fact]
    public void ReadClampsToAvailableWordsAndMaxCount()
    {
        uint[] words = [5, 1, 2, 3];
        var pageFaults = new PageFaults();

        ReadOnlySpan<uint> nodes = pageFaults.Read(
            MemoryMarshal.AsBytes(words.AsSpan()),
            maxCount: 2);

        Assert.Equal([1u, 2u], nodes.ToArray());
    }
}