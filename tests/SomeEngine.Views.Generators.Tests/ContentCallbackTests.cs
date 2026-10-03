using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ContentCallbackTests
{
    [Fact]
    public void GenericContentKeepsItsDisplayAndUsesTheCurrentCallbacksPassedByItsContainer()
    {
        using var frames = new HeadlessFrameDriver();
        var refresh = new State<int>(0);
        var clicked = new State<int>(-1);
        ContentCallbackScreens.Decoration = " old";
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentCallbackScreens.Screen_View(refresh, clicked));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity content = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        var button = frames.World.ReadSparse<ContentButton<int>>(content);
        Assert.Equal("3 old", button.Text);

        ContentCallbackScreens.Decoration = " new";
        refresh.Value = 1;
        frames.Frame();

        Assert.Equal("1", frames.World.ReadSparse<ComposeText>(container).Value);
        var current = frames.World.ReadSparse<ContentButton<int>>(content);
        Assert.Equal("3 old", current.Text);
        Assert.Equal("tick 1", current.Tooltip());
        button.Change(7);
        Assert.Equal(1, clicked.Value);
    }
}

public readonly record struct ContentButton<T>(string Text, Action<T> Change, Func<string> Tooltip) : ISparseComponent;

public static partial class ContentCallbackScreens
{
    public static string Decoration = " old";

    [Composable]
    public static void Screen(State<int> refresh, State<int> clicked) =>
        Container(3, refresh.Value, clicked, static (value, change, tooltip) =>
            Scope.Write<ContentButton<int>>(Scope.Entity) = new(value + Decoration, change, tooltip));

    [Composable]
    public static void Container<T>(T value, int tick, State<int> clicked,
        [Composable] Action<T, Action<T>, Func<string>> content)
    {
        Scope.Write<ComposeText>(Scope.Entity) = new(tick.ToString());
        content(value, _ => clicked.Value = tick, () => "tick " + tick);
    }
}
