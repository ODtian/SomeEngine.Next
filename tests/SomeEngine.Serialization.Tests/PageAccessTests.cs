using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class PageAccessTests
{
    [Fact]
    public void AWriteExcludesEveryBufferInTheBranchAndAllowsAnIndependentBranch()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        var access = new PageAccess();
        using var numbers = new PageList<int>(arena, access);
        using var bytes = new PageBuffer(arena, 128, access);
        numbers.Add(31);
        using var other = bytes.Duplicate();
        numbers.Write(0, 1, (_, values) =>
        {
            Assert.Throws<InvalidOperationException>(() => bytes[0]);
            Assert.Throws<InvalidOperationException>(() => bytes.Duplicate());
            Assert.Throws<InvalidOperationException>(() => bytes.Dispose());
            Assert.Throws<InvalidOperationException>(() => Task.Run(() => bytes.Duplicate()).GetAwaiter().GetResult());
            values[0] = 42;
            other[0] = 7;
        });
        Assert.Equal(42, numbers[0]);
        Assert.Equal(0, bytes[0]);
        Assert.Equal(7, other[0]);
    }

    [Fact]
    public async Task AConcurrentAppendAndDuplicateAlwaysProduceAConsistentCountAndContent()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        using var values = new PageList<int>(arena);
        var append = Task.Run(() => { for (int i = 0; i < 5000; i++) values.Add(i); });
        for (int i = 0; i < 100; i++)
        {
            using var copy = values.Duplicate();
            int[] items = copy.ToArray();
            Assert.Equal(items.Length, copy.Count);
            for (int j = 0; j < items.Length; j++) Assert.Equal(j, items[j]);
        }
        await append;
        Assert.Equal(5000, values.Count);
    }
}
