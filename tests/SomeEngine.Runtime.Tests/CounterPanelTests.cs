using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Job;
using SomeEngine.Layout;

namespace SomeEngine.Runtime.Tests;

public sealed class CounterPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_example_button_updates_its_visible_count_and_keeps_it_when_resized(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        using var panel = new SurfacePanelExample(world, 1280, 720);
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);
        frames.UpdateInput();
        frames.UpdateViews(0);
        var label = world.Read<LayoutRect>(FindText(world, "Add one")).Value;
        var position = new Vector2(label.X + label.Width / 2, label.Y + label.Height / 2);

        input.ChangePointerButton(0, true, position);
        input.ChangePointerButton(0, false, position);
        frames.UpdateInput();
        frames.UpdateViews(1d / 60);

        Assert.Equal(1, panel.ClickCount);
        FindText(world, "Clicks: 1");
        panel.Resize(960, 540);
        frames.UpdateViews(1d / 60);
        Assert.Equal(1, panel.ClickCount);
        FindText(world, "Clicks: 1");
    }

    private static Entity FindText(World world, string text)
    {
        var query = world.Query(world.QueryDefinition().Read<TextContent>());
        var result = (Text: text, Node: Entity.Null);
        try
        {
            world.ExecuteQuery(query, ref result, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                    if (row.Read<TextContent>().Value == value.Text)
                        value.Node = row.Entity;
            });
        }
        finally { world.ReleaseQuery(query); }
        Assert.NotEqual(Entity.Null, result.Node);
        return result.Node;
    }
}
