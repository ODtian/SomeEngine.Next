using System.Buffers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class PageBufferTests
{
    private static PageArena CreateArena(long budget = 1024 * 1024) => new(
        new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = budget }),
        new() { MinimumPageSize = 16, MaximumPageSize = 512, AllocationSize = 4096 });

    [Fact]
    public void GradesPagesReusesEmptySpaceAndReportsRetainedAllocations()
    {
        using var arena = CreateArena();
        using (var large = new PageBuffer(arena, 1024))
        using (var small = new PageBuffer(arena, 17))
        {
            Assert.Equal(1056, arena.Statistics.ActiveBytes);
            Assert.Equal(1056, arena.Residency.Used(ResidencyClass.DecodedCpu));
            large.Write(0, 1024, static (_, bytes) => bytes.Fill(7));
            small.Write(0, 17, static (_, bytes) => bytes.Fill(8));
            arena.Trim();
            Assert.Equal(8192, arena.Statistics.AllocatedBytes);
        }
        Assert.Equal(0, arena.Statistics.ActiveBytes);
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
        using (var again = new PageBuffer(arena, 17))
        {
            using var read = again.Retain();
            Assert.Equal(new byte[17], read.ToArray());
            Assert.Equal(8192, arena.Statistics.AllocatedBytes);
        }
        arena.Trim();
        Assert.Equal(0, arena.Statistics.AllocatedBytes);
    }

    [Fact]
    public void BranchAndRangeRetainForceOnlyAffectedPageCopies()
    {
        using var arena = CreateArena();
        using var original = new PageBuffer(arena, 64 * 1024);
        original.Write(0, original.Length, static (offset, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(offset + i);
        });
        using var branch = original.Duplicate();
        using var smallRead = original.Retain(512 * 50 + 3, 17);
        long copyBytes = arena.Statistics.CopiedBytes;
        branch[512 * 50 + 3] = 222;
        Assert.Equal(512, arena.Statistics.CopiedBytes - copyBytes);
        Assert.Equal(3, original[512 * 50 + 3]);
        Assert.Equal(3, smallRead.Sequence.FirstSpan[0]);
        branch[512 * 50 + 4] = 111;
        Assert.Equal(512, arena.Statistics.CopiedBytes - copyBytes);
        original[512 * 50 + 3] = 31;
        Assert.Equal(3, smallRead.Sequence.FirstSpan[0]);
        Assert.Equal(222, branch[512 * 50 + 3]);
        Assert.Equal(1024, arena.Statistics.CopiedBytes - copyBytes);
    }

    [Fact]
    public void SmallReadPinsOnlyIntersectingPagesAndSurvivesBranchAndArenaDisposal()
    {
        var arena = CreateArena();
        var buffer = new PageBuffer(arena, 8192);
        buffer[1500] = 37;
        using var read = buffer.Retain(1500, 1);
        buffer.Dispose();
        arena.Dispose();
        Assert.Equal(37, read.Sequence.FirstSpan[0]);
        Assert.Equal(512, arena.Statistics.ActiveBytes);
        Assert.Equal(4096, arena.Statistics.AllocatedBytes);
        Assert.Equal(3584, arena.Statistics.IdleBytes);
        read.Dispose();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, arena.Statistics.AllocatedBytes);
        Assert.Throws<ObjectDisposedException>(() => read.ToArray());
    }

    [Fact]
    public void BatchWriteBlocksReentrantSharingRebindingAndDisposal()
    {
        using var arena = CreateArena();
        using var buffer = new PageBuffer(arena, 1100);
        using var previous = buffer.Duplicate();
        long visited = 0;
        buffer.Write(100, 900, (offset, bytes) =>
        {
            Assert.Equal(visited, offset);
            Assert.Throws<InvalidOperationException>(() => buffer.Duplicate());
            Assert.Throws<InvalidOperationException>(() => buffer.Retain());
            Assert.Throws<InvalidOperationException>(() => buffer.Resize(1));
            Assert.Throws<InvalidOperationException>(() => buffer.Remove(0, 1));
            Assert.Throws<InvalidOperationException>(() => buffer.Dispose());
            Assert.Throws<InvalidOperationException>(() => Task.Run(() => buffer.Duplicate()).GetAwaiter().GetResult());
            bytes.Fill(9);
            visited += bytes.Length;
        });
        Assert.Equal(900, visited);
        Assert.Equal(0, previous[500]);
        Assert.Equal(9, buffer[500]);
        Assert.Throws<IOException>(() => buffer.Write(0, 1, static (_, _) => throw new IOException()));
        buffer[0] = 3;
        Assert.Equal(3, buffer[0]);
    }

    [Fact]
    public void BoundaryValuesAndSequencesRoundTripWithoutPerValueAllocations()
    {
        using var arena = CreateArena();
        using var buffer = new PageBuffer(arena, 2048);
        buffer.Write(509, 0x7192345678901234L);
        Assert.Equal(0x7192345678901234L, buffer.Read<long>(509));
        using var read = buffer.Retain(506, 514);
        Assert.Equal(514, read.Sequence.Length);
        Assert.Equal(buffer.Read<long>(509), new BinaryDataReader(read.Sequence.Slice(3, 8)).ReadInt64());
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long value = 0;
        for (int i = 0; i < 1000; i++) value = buffer.Read<long>(509);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);
        Assert.Equal(0x7192345678901234L, value);
    }

    [Fact]
    public void RandomStructuralEditsAndBranchesMatchAnOrdinaryList()
    {
        using var arena = CreateArena();
        using var buffer = new PageBuffer(arena);
        List<byte> expected = [];
        var random = new Random(739);
        for (int iteration = 0; iteration < 600; iteration++)
        {
            using var previous = buffer.Duplicate();
            byte[] before = expected.ToArray();
            int offset = random.Next(expected.Count + 1);
            if (iteration % 3 == 0)
            {
                int count = random.Next(expected.Count - offset + 1);
                buffer.Remove(offset, count);
                expected.RemoveRange(offset, count);
            }
            else
            {
                byte[] inserted = new byte[random.Next(1, 1200)];
                random.NextBytes(inserted);
                buffer.Insert(offset, inserted);
                expected.InsertRange(offset, inserted);
            }
            using var currentRead = buffer.Retain();
            using var previousRead = previous.Retain();
            Assert.Equal(expected, currentRead.ToArray());
            Assert.Equal(before, previousRead.ToArray());
        }
        buffer.Resize(0);
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void BudgetFailureDoesNotLoseTheOldContentOrLeakReservations()
    {
        using var arena = CreateArena(512);
        using var buffer = new PageBuffer(arena, 512);
        buffer[0] = 19;
        using var branch = buffer.Duplicate();
        Assert.Throws<InvalidOperationException>(() => branch[0] = 29);
        Assert.Throws<InvalidOperationException>(() => buffer.Resize(600));
        Assert.Equal(19, buffer[0]);
        Assert.Equal(19, branch[0]);
        Assert.Equal(512, buffer.Length);
        Assert.Equal(512, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void StandardWriterCommitsBytesWithoutPublishingWritableAliases()
    {
        using var arena = CreateArena();
        using var writer = new PageBufferWriter(arena);
        Memory<byte> escaped = writer.GetMemory(4);
        escaped.Span[..4].Fill(7);
        writer.Advance(4);
        using var branch = writer.Duplicate();
        using var read = writer.Retain();
        escaped.Span[..4].Fill(9);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, read.ToArray());
        Assert.Equal(7, branch[0]);
        writer.Dispose();
        Assert.Equal(7, branch[0]);
    }

    [Fact]
    public void ExistingCodecUsesPagedOutputAndSegmentedInput()
    {
        using var arena = CreateArena();
        using var writer = new PageBufferWriter(arena);
        var value = new GeneratedCurrentContract { Id = 17, Name = "跨页", Values = Enumerable.Range(0, 7000).ToArray() };
        BinarySerializer.Write(writer, value);
        using var read = writer.Retain();
        var result = BinarySerializer.Deserialize<GeneratedCurrentContract>(read.Sequence);
        Assert.Equal(value.Id, result.Id);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Values, result.Values);
        Assert.InRange(arena.Statistics.ActiveBytes, writer.WrittenCount, writer.WrittenCount + 1024);
    }

    [Fact]
    public void ValueCollectionAssignmentsInsertsAndBatchesIsolateBranches()
    {
        using var arena = CreateArena();
        using var values = new PageList<ThreeInts>(arena);
        for (int i = 0; i < 300; i++) values.Add(new(i, i + 1, i + 2));
        using var earlier = values.Duplicate();
        values[111] = new(7, 8, 9);
        values.Insert(100, new(1000, 1001, 1002));
        values.RemoveAt(17);
        ThreeInts[] before = values.ToArray();
        values.Write(12, 180, (offset, items) =>
        {
            Assert.Throws<InvalidOperationException>(() => values.Duplicate());
            for (int i = 0; i < items.Length; i++) items[i] = items[i] with { Z = checked((int)offset + i) };
        });
        Assert.Equal(new ThreeInts(111, 112, 113), earlier[111]);
        Assert.Equal(before[100].X, values[100].X);
        Assert.Equal(88, values[100].Z);
        Assert.Equal(new ThreeInts(299, 300, 301), values[299]);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        foreach (var value in values) total += value.X;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);
        Assert.Equal(values.ToArray().Sum(v => v.X), total);
    }

    private readonly record struct ThreeInts(int X, int Y, int Z);
}
