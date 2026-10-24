using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeStateTests
{
    [Fact]
    public void PassingStateToChildrenRefreshesTheReaderAndKeepsOtherContent()
    {
        using var frames = new HeadlessFrameDriver();
        var state = MutableStateOf(3);
        StateScreens.Decoration = " old";
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateScreens.Screen_View(state));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("3 old", frames.World.ReadSparse<ComposeText>(children[0]).Value);

        StateScreens.Decoration = " new";
        frames.World.ReadSparse<StateScreens.Controls_View>(children[1]).state.Value = 7;
        frames.Frame();

        Assert.Equal("7 new", frames.World.ReadSparse<ComposeText>(children[0]).Value);
        Assert.Equal("root old", frames.World.ReadSparse<ComposeText>(root).Value);
        Assert.Equal("controls old", frames.World.ReadSparse<ComposeText>(children[1]).Value);
    }

    [Fact]
    public void LiftedStateChangesTheChildValueWhileEqualValuesKeepContentAndRefreshCallbacks()
    {
        using var frames = new HeadlessFrameDriver();
        var refresh = MutableStateOf(0);
        var selectedTick = MutableStateOf(-1);
        LiftedScreens.Decoration = " old";
        Entity root = Views.Spawn(frames.World, Entity.Null, new LiftedScreens.Screen_View(refresh, selectedTick));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("False old", frames.World.ReadSparse<ComposeText>(child).Value);

        LiftedScreens.Decoration = " new";
        refresh.Value = 1;
        frames.Frame();

        Assert.Equal("1", frames.World.ReadSparse<ComposeText>(root).Value);
        Assert.Equal("False old", frames.World.ReadSparse<ComposeText>(child).Value);
        var button = frames.World.ReadSparse<ToggleCallbacks>(child);
        Assert.Equal("tick 1", button.Tooltip());
        button.Change(true);
        frames.Frame();
        Assert.Equal(1, selectedTick.Value);
        Assert.Equal("True new", frames.World.ReadSparse<ComposeText>(child).Value);
    }
}

public readonly record struct ComposeText(string Value) : ISparseComponent;
public readonly record struct ToggleCallbacks(Action<bool> Change, Func<string> Tooltip) : ISparseComponent;

public static partial class LiftedScreens
{
    public static string Decoration = " old";

    [Composable]
    public static void Screen(State<int> refresh, State<int> selectedTick)
    {
        var paused = Remember(static () => MutableStateOf(false));
        int tick = refresh.Value;
        Scope.Write<ComposeText>(Scope.Entity) = new(tick.ToString());
        Toggle(paused.Value, next =>
        {
            paused.Value = next;
            selectedTick.Value = tick;
        }, () => "tick " + tick);
    }

    [Composable]
    public static void Toggle(bool value, Action<bool> change, Func<string> tooltip)
    {
        Scope.Write<ComposeText>(Scope.Entity) = new(value + Decoration);
        Scope.Write<ToggleCallbacks>(Scope.Entity) = new(change, tooltip);
    }
}

public static partial class StateScreens
{
    public static string Decoration = " old";

    [Composable]
    public static void Screen(State<int> state)
    {
        Scope.Write<ComposeText>(Scope.Entity) = new("root" + Decoration);
        Reader(state);
        Controls(state);
    }

    [Composable]
    public static void Reader(State<int> state) =>
        Scope.Write<ComposeText>(Scope.Entity) = new(state.Value + Decoration);

    [Composable]
    public static void Controls(State<int> state) =>
        Scope.Write<ComposeText>(Scope.Entity) = new("controls" + Decoration);
}
