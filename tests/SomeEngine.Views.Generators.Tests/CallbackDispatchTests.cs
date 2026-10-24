using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.Generators.Tests;

public sealed class CallbackDispatchTests
{
    [Fact]
    public void PublishedGenericCallbacksKeepTheirArgumentsAndReturnTheCurrentResultAfterParameterReplacement()
    {
        using var frames = new HeadlessFrameDriver();
        var selected = new State<string>("");
        Entity root = Views.Spawn(frames.World, Entity.Null, new GenericCallbackScreens<string>.Display_View<int>(3,
            (text, number) => selected.Value = text + " " + number + " before", number => "before " + number));
        frames.Frame();
        var callbacks = frames.World.ReadSparse<CallbackPair<string, int>>(root);

        frames.World.ReplaceSparse(root, new GenericCallbackScreens<string>.Display_View<int>(3,
            (text, number) => selected.Value = text + " " + number + " after", number => "after " + number));
        frames.Frame();

        Assert.Equal("after 5", callbacks.Text(5));
        callbacks.Change("chosen", 7);
        Assert.Equal("chosen 7 after", selected.Value);
    }

    [Fact]
    public void PublishedCallbacksFollowRestoredChildParametersAfterAParentBuildFails()
    {
        using var frames = new HeadlessFrameDriver();
        var clicked = new State<int>(-1);
        Entity root = Views.Spawn(frames.World, Entity.Null, new CallbackRecoveryScreens.Screen_View(0, false, clicked));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        var callbacks = frames.World.ReadSparse<ToggleCallbacks>(child);

        frames.World.ReplaceSparse(root, new CallbackRecoveryScreens.Screen_View(1, true, clicked));
        frames.Frame();

        Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal("tick 0", callbacks.Tooltip());
        callbacks.Change(true);
        Assert.Equal(0, clicked.Value);

        frames.World.ReplaceSparse(root, new CallbackRecoveryScreens.Screen_View(2, false, clicked));
        frames.Frame();

        Assert.Equal("tick 2", callbacks.Tooltip());
        callbacks.Change(true);
        Assert.Equal(2, clicked.Value);
    }
}

public readonly record struct CallbackPair<TText, TValue>(Action<TText, TValue> Change, Func<TValue, TText> Text) : ISparseComponent;

public static partial class GenericCallbackScreens<T> where T : class
{
    [Composable]
    public static void Display<TValue>(TValue value, Action<T, TValue> change, Func<TValue, T> text) where TValue : struct =>
        Scope.Write<CallbackPair<T, TValue>>(Scope.Entity) = new(change, text);
}

public static partial class CallbackRecoveryScreens
{
    [Composable]
    public static void Screen(int tick, bool fail, State<int> clicked)
    {
        Toggle(false, _ => clicked.Value = tick, () => "tick " + tick);
        if (fail)
            throw new InvalidOperationException("parent failed");
    }

    [Composable]
    public static void Toggle(bool value, Action<bool> change, Func<string> tooltip) =>
        Scope.Write<ToggleCallbacks>(Scope.Entity) = new(change, tooltip);
}
