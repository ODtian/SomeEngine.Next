using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class TypedPageTests
{
    private static PageArena CreateArena() => new(new ResidencyBudgetLedger(),
        new() { MinimumPageSize = 64, MaximumPageSize = 512, AllocationSize = 4096 });

    [Fact]
    public void ReferenceElementsAndNestedImmutableValuesIsolateElementAssignments()
    {
        using var arena = CreateArena();
        using var values = new PageList<Entry>(arena);
        for (int i = 0; i < 1000; i++) values.Add(new(i, "value " + i, ["original"]));
        using var earlier = values.Duplicate();
        using PageRange<Entry> range = values.Retain();
        long copies = arena.Statistics.CopiedBytes;
        values[99] = values[99] with { Labels = values[99].Labels.Add("changed") };
        values.Insert(100, new(-1, "inserted", []));
        values.RemoveAt(17);
        Assert.Equal(["original"], earlier[99].Labels);
        Assert.Equal(["original", "changed"], values[98].Labels);
        Assert.Equal(-1, values[99].Id);
        Assert.Equal(1000, range.Sequence.Length);
        Assert.Equal(1000, earlier.Count);
        Assert.InRange(arena.Statistics.CopiedBytes - copies, 1, 1024);

        int firstTraversal = 0;
        foreach (Entry entry in earlier) firstTraversal += entry.Id;
        Assert.Equal(499500, firstTraversal);

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        foreach (Entry entry in earlier) total += entry.Id;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(499500, total);
        Assert.Equal(0, allocated);
        values.Dispose();
        earlier.Dispose();
        arena.Dispose();
        Assert.Equal("value 0", range.Sequence.FirstSpan[0].Text);
        range.Dispose();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void ReferenceContainingStructsUseTypedSpansAcrossDifferentPageSizes()
    {
        using var arena = CreateArena();
        using var values = new PageList<NamedValue>(arena);
        for (int i = 0; i < 300; i++) values.Add(new("entry " + i, i, new Entry(i, "tag", [])));
        using var before = values.Duplicate();
        values.Write(7, 240, static (offset, entries) =>
        {
            for (int i = 0; i < entries.Length; i++) entries[i] = entries[i] with { Number = offset + i, Name = "written" };
        });
        Assert.Equal(7, before[7].Number);
        Assert.Equal("entry 7", before[7].Name);
        Assert.Equal(0, values[7].Number);
        Assert.Equal("written", values[7].Name);
        Assert.Equal(246, before[246].Number);
        Assert.Equal(239, values[246].Number);
        Assert.Equal(299, values[299].Number);
        Assert.True(arena.Statistics.BackingBytes < arena.Statistics.AllocatedBytes);
        using var read = values.Retain();
        Assert.Equal(values.ToArray(), read.ToArray());
        GC.Collect();
        Assert.Equal(171, ((Entry)values[171].Tag!).Id);
    }

    [Fact]
    public void ActiveArraysKeepReferencesAliveAndReturnedArraysReleaseThem()
    {
        using var arena = CreateArena();
        using var values = new PageList<Entry>(arena);
        WeakReference<Entry> active = AddValue(values);
        GC.Collect();
        Assert.True(active.TryGetTarget(out _));
        WeakReference<Entry> released = AddAndRelease(arena);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(released.TryGetTarget(out _));
        Assert.Equal(23, values[0].Id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Entry> AddValue(PageList<Entry> values)
    {
        var entry = new Entry(23, "alive", []);
        values.Add(entry);
        return new(entry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Entry> AddAndRelease(PageArena arena)
    {
        using var values = new PageList<Entry>(arena);
        var entry = new Entry(47, "released", []);
        values.Add(entry);
        return new(entry);
    }

    private sealed record Entry(int Id, string Text, ImmutableList<string> Labels);
    private readonly record struct NamedValue(string Name, long Number, object? Tag);
}
