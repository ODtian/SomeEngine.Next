using System.Buffers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class PageBufferMapTests
{
    [Fact]
    public void BranchWritesDetachOnlyTheirPageAndReuseItForTheNextWrite()
    {
        var budget = new ResidencyBudgetLedger();
        using var arena = new PageArena(budget, new() { MaximumPageSize = 1024 });
        using var original = new PageBufferMap(arena);
        for (ulong key = 0; key < 1000; key++) original.Set(key, new byte[8192]);
        long copied = arena.Statistics.CopiedBytes;
        using var branch = original.Duplicate();
        Assert.Equal(copied, arena.Statistics.CopiedBytes);
        branch.Write(511, 1100, new byte[] { 41 });
        Assert.Equal(copied + 1024, arena.Statistics.CopiedBytes);
        branch.Write(511, 1101, new byte[] { 42 });
        Assert.Equal(copied + 1024, arena.Statistics.CopiedBytes);
        using PageRange<byte> old = original.Retain(511, 1100, 2);
        using PageRange<byte> changed = branch.Retain(511, 1100, 2);
        Assert.Equal(new byte[] { 0, 0 }, old.Sequence.ToArray());
        Assert.Equal(new byte[] { 41, 42 }, changed.Sequence.ToArray());
        original.Dispose();
        branch.Dispose();
        Assert.Equal(new byte[] { 41, 42 }, changed.Sequence.ToArray());
        old.Dispose(); changed.Dispose();
        Assert.Equal(0, budget.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void ReplacementRemovalAndKeyEnumerationPreserveOtherBranches()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        using var values = new PageBufferMap(arena);
        var expected = new SortedDictionary<ulong, byte>();
        var random = new Random(219);
        for (int i = 0; i < 2000; i++)
        {
            ulong key = (ulong)random.Next(200);
            if (random.Next(3) == 0) Assert.Equal(expected.Remove(key), values.Remove(key));
            else { byte value = (byte)i; values.Set(key, new byte[] { value }); expected[key] = value; }
            if (i % 41 != 0) continue;
            using var branch = values.Duplicate();
            branch.Set(201, new byte[] { 7 });
            Assert.Equal(expected.Keys, values.Keys);
            foreach ((ulong storedKey, byte value) in expected)
            {
                using PageRange<byte> bytes = branch.Retain(storedKey);
                Assert.Equal(value, bytes.Sequence.FirstSpan[0]);
            }
        }
        Assert.Equal(expected.Keys, values.Keys);
    }

    [Fact]
    public void BatchWriteExcludesSharingAndSiblingFieldsUntilItReturns()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        var access = new PageAccess();
        using var values = new PageBufferMap(arena, access);
        using var field = new PageBuffer(arena, 8, access);
        values.Set(1, new byte[10]);
        values.Write(1, 0, 10, (_, bytes) =>
        {
            Assert.Throws<InvalidOperationException>(() => values.Duplicate());
            Assert.Throws<InvalidOperationException>(() => values.Retain(1));
            Assert.Throws<InvalidOperationException>(() => Task.Run(() => field.Duplicate()).GetAwaiter().GetResult());
            bytes.Fill(3);
        });
        using PageRange<byte> result = values.Retain(1);
        Assert.Equal(Enumerable.Repeat((byte)3, 10), result.Sequence.ToArray());
    }
}
