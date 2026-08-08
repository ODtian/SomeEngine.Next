using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class PageHeapTests
{
    [Fact]
    public void FreeBlockIsReusedForLaterAllocation()
    {
        var heap = new PageHeap();
        Assert.True(heap.TryAlloc(64, out uint first));
        Assert.True(heap.TryAlloc(64, out uint second));

        heap.Free(first, 64);
        Assert.True(heap.TryAlloc(32, out uint reused));

        Assert.Equal(first, reused);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void InvalidOrDuplicateReleaseFailsClosed()
    {
        var heap = new PageHeap(64);
        Assert.True(heap.TryAlloc(16, out uint offset));
        heap.Free(offset, 16);

        Assert.Throws<InvalidOperationException>(() => heap.Free(offset, 16));
        Assert.Throws<ArgumentException>(() => heap.Free(1, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Free(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.TryAlloc(uint.MaxValue, out _));
    }
}
