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

    [Fact]
    public void ByteWritesPreserveTheBorrowedInputAndOtherReaders()
    {
        byte[] input = [1, 2, 3];
        using var first = new SharedBytes(input);
        using var second = first.Retain();
        using var reader = new BufferRead(first);
        first.Write().Span[0] = 4;
        second.Write().Span[1] = 5;
        Assert.Equal(new byte[] { 1, 2, 3 }, input);
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Memory.ToArray());
        Assert.Equal(new byte[] { 4, 2, 3 }, first.Read.ToArray());
        Assert.Equal(new byte[] { 1, 5, 3 }, second.Read.ToArray());
    }

    [Fact]
    public void ExclusiveByteWritesRetainPreviousChanges()
    {
        using var bytes = new SharedBytes(new byte[] { 1, 2 });
        bytes.Write().Span[0] = 3;
        bytes.Write().Span[1] = 4;
        Assert.Equal(new byte[] { 3, 4 }, bytes.Read.ToArray());
    }
}
