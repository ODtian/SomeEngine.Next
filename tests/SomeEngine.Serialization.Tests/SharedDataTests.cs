using SomeEngine.Serialization.Containers;

namespace SomeEngine.Serialization.Tests;

public sealed class SharedDataTests
{
    [Fact]
    public void RetainedReferenceRemainsReadableUntilItsRelease()
    {
        var first = new SharedData<string>("content");
        var second = first.Retain();
        Assert.True(first.IsShared);
        Assert.False(first.Release(out _));
        Assert.Equal("content", second.Read);
        Assert.False(second.IsShared);
        Assert.True(second.Release(out string? released));
        Assert.Equal("content", released);
        Assert.False(second.Release(out _));
        Assert.Throws<ObjectDisposedException>(() => second.Read);
    }

}
