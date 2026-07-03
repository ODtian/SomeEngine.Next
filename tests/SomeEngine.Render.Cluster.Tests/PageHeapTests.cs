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
}