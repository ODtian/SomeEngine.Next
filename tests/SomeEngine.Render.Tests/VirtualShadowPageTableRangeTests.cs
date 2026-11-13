using SomeEngine.ECS;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowPageTableRangeTests
{
    [Fact]
    public void AddingAnotherLightPreservesExistingProjectionRanges()
    {
        using var world = new World();
        var directional = new VirtualShadowLightKey(VirtualShadowLightKind.Directional, world.CreateEntity(), 0, 7);
        var point = new VirtualShadowLightKey(VirtualShadowLightKind.Point, world.CreateEntity(), 3, 0);
        var table = new VirtualShadowPageTableRanges();
        var first = new PageTableRange[1];
        table.WriteRanges([directional], [64u], first);
        table.Apply([directional], first);

        var next = new PageTableRange[2];
        uint requiredEntries = table.WriteRanges([point, directional], [85u, 64u], next);
        table.Apply([point, directional], next);

        Assert.Equal(first[0], next[1]);
        Assert.True(table.TryGetRange(directional, out PageTableRange current));
        Assert.Equal(first[0], current);
        Assert.True(table.TryGetRange(point, out PageTableRange added));
        Assert.NotEqual(current.RangeId, added.RangeId);
        Assert.True(added.Offset >= current.Offset + current.EntryCount);
        Assert.Equal(requiredEntries, table.PageTableEntryCount);
    }

    [Fact]
    public void ReusingReleasedEntriesAdvancesTheRangeGeneration()
    {
        using var world = new World();
        var directional = new VirtualShadowLightKey(VirtualShadowLightKind.Directional, world.CreateEntity(), 0, 7);
        var spot = new VirtualShadowLightKey(VirtualShadowLightKind.Spot, world.CreateEntity(), 0, 0);
        var table = new VirtualShadowPageTableRanges();
        var first = new PageTableRange[1];
        table.WriteRanges([directional], [64u], first);
        table.Apply([directional], first);
        table.Apply([], []);

        var reused = new PageTableRange[1];
        uint requiredEntries = table.WriteRanges([spot], [48u], reused);
        table.Apply([spot], reused);

        Assert.Equal(first[0].RangeId, reused[0].RangeId);
        Assert.Equal(first[0].Offset, reused[0].Offset);
        Assert.Equal(first[0].EntryCount, reused[0].EntryCount);
        Assert.Equal(first[0].RangeGeneration + 1u, reused[0].RangeGeneration);
        Assert.Equal(first[0].EntryCount, requiredEntries);
        Assert.True(table.TryGetRange(spot, out PageTableRange current));
        Assert.Equal(reused[0], current);
    }

    [Fact]
    public void FrameRangesBecomeVisibleWhenAppliedAfterSubmission()
    {
        using var world = new World();
        var directional = new VirtualShadowLightKey(VirtualShadowLightKind.Directional, world.CreateEntity(), 0, 7);
        var point = new VirtualShadowLightKey(VirtualShadowLightKind.Point, world.CreateEntity(), 0, 0);
        var table = new VirtualShadowPageTableRanges();
        var first = new PageTableRange[1];
        table.WriteRanges([directional], [64u], first);
        table.Apply([directional], first);

        var discarded = new PageTableRange[2];
        uint requestedEntries = table.WriteRanges([directional, point], [64u, 85u], discarded);

        Assert.Equal(149u, requestedEntries);
        Assert.Equal(64u, table.PageTableEntryCount);
        Assert.True(table.TryGetRange(directional, out PageTableRange current));
        Assert.Equal(first[0], current);
        Assert.False(table.TryGetRange(point, out _));

        var submitted = new PageTableRange[1];
        Assert.Equal(64u, table.WriteRanges([directional], [64u], submitted));
        table.Apply([directional], submitted);

        Assert.Equal(first, submitted);
        Assert.Equal(64u, table.PageTableEntryCount);
        Assert.False(table.TryGetRange(point, out _));
    }
}
