using DefaultGame;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Job;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Runtime.Tests;

public sealed class HudOverlayTests
{
    [Fact]
    public void Measuring_a_view_frame_updates_the_displayed_numbers_and_layout()
    {
        using var world = new World();
        using var hud = new HudOverlay(world, 1280, 720);
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        frames.UpdateViews(0);
        var rectangle = world.ReadComponentData<LayoutRect>(FindText(world, "N00:000")).Value;

        hud.Update(42);
        frames.MeasureViews(1d / 60);

        Assert.Equal(rectangle, world.ReadComponentData<LayoutRect>(FindText(world, "N00:042")).Value);
        FindText(world, "N99:042");
    }

    [Fact]
    public void The_long_list_scrolls_through_visible_rows_and_keeps_rows_that_remain_visible()
    {
        using var world = new World();
        using var hud = new HudOverlay(world, 1280, 720);
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        frames.UpdateViews(0);
        Entity list = FindList(world, hud.Canvas);
        Assert.NotEqual(Entity.Null, list);
        Entity[] before = ReadChildren(world, list);
        Assert.Equal(11, before.Length);
        Assert.Equal("Item 00000", world.ReadComponentData<TextContent>(before[0]).Value);

        var rectangle = world.ReadComponentData<LayoutRect>(list).Value;
        using var input = new WindowInput(world);
        input.ScrollPointer(new Vector2(rectangle.X + 8, rectangle.Y + 8), new Vector2(0, 48));
        frames.UpdateInput();
        frames.UpdateViews(1d / 60);
        frames.UpdateViews(1d / 60);

        Entity[] after = ReadChildren(world, list);
        Assert.Equal("Item 00001", world.ReadComponentData<TextContent>(after[0]).Value);
        Assert.Equal(before[1], after[0]);
        Assert.False(world.Has<global::SomeEngine.Views.ViewType>(before[0]));
        Assert.Equal("Item 00012", world.ReadComponentData<TextContent>(after[^1]).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_standard_hud_displays_a_thousand_elements_and_updates_numbers_across_resizes(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        using var hud = new HudOverlay(world, 1280, 720);
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        frames.UpdateViews(0);

        Assert.InRange(CountChildren(world, hud.Canvas), 1000, 1050);
        FindText(world, "N00:000");
        FindText(world, "799");
        hud.Update(7);
        frames.UpdateViews(1d / 60);
        FindText(world, "N00:007");
        FindText(world, "N99:007");

        hud.Resize(960, 540);
        frames.UpdateViews(1d / 60);
        FindText(world, "N00:007");
        FindText(world, "799");
        Assert.Equal(new Size(960, 540), world.ReadComponentData<Canvas>(hud.Canvas).Size);
    }

    [Fact]
    public void The_scene_platform_preserves_the_selected_scene_when_changing_the_hud()
    {
        var platform = new SceneTestPlatform([
            new TestScene("1024 spheres", ".", Animate: true),
            new TestScene("Sponza", "."),
            new TestScene("Amazon Bistro", "."),
        ]);
        platform.SelectScene(1);
        platform.SetHudOverlay(true);

        Assert.True(platform.HudOverlayEnabled);
        Assert.True(platform.HudNumbersChange);
        Assert.Equal(1, platform.SelectedScene);

        platform.SetHudOverlay(true, changeNumbers: false);
        Assert.False(platform.HudNumbersChange);
        platform.SetHudOverlay(false);
        Assert.False(platform.HudOverlayEnabled);
        Assert.Equal(1, platform.SelectedScene);
    }

    private static Entity[] ReadChildren(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }

    private static int CountChildren(World world, Entity parent)
    {
        int count = 0;
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        foreach (Entity child in children)
            count += 1 + CountChildren(world, child);
        return count;
    }

    private static Entity FindList(World world, Entity parent)
    {
        if (world.HasSparse<VirtualList>(parent)) return parent;
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        foreach (Entity child in children)
        {
            Entity list = FindList(world, child);
            if (list != Entity.Null) return list;
        }
        return Entity.Null;
    }

    private static Entity FindText(World world, string text)
    {
        Query query = world.Query(new QueryDescription().With<TextContent>());
        var result = (Text: text, Node: Entity.Null);
        using QueryScope source = query.OpenRead();
        foreach (QueryChunk chunk in source)
        {
            ReadOnlySpan<TextContent> values = chunk.Read<TextContent>();
            for (int row = 0; row < chunk.Count; row++)
                if (values[row].Value == text) result.Node = chunk.Entities[row];
        }
        Assert.NotEqual(Entity.Null, result.Node);
        return result.Node;
    }
}
