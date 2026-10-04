using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeGroupScopeTests
{
    [Fact]
    public void KeyedGroupsKeepTheirNodesAndValuesWithReorderedItems()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new GroupedComposition.Items_View([1, 2]));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        frames.World.ReadSparse<CounterLabel<int>>(children[0]).Value.Value = 11;
        frames.World.ReadSparse<CounterLabel<int>>(children[1]).Value.Value = 22;

        frames.World.ReplaceSparse(root, new GroupedComposition.Items_View([2, 1]));
        frames.Frame();

        Entity[] reordered = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal([children[1], children[0]], reordered);
        Assert.Equal([22, 11], reordered.Select(child => frames.World.ReadSparse<CounterNumber<int>>(child).Value));
    }

    [Fact]
    public void CaughtGroupErrorsPreserveFollowingRememberedValuesAndNodes()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new GroupedComposition.Recovery_View(false));
        frames.Frame();
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        frames.World.ReadSparse<CounterLabel<int>>(child).Value.Value = 22;

        frames.World.ReplaceSparse(root, new GroupedComposition.Recovery_View(true));
        frames.Frame();

        Assert.Equal(child, Hierarchy<ElementTree>.GetChildren(frames.World, root).Single());
        Assert.Equal(22, frames.World.ReadSparse<CounterNumber<int>>(child).Value);
        Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<FailureError>(root).Value);
    }
}

public readonly record struct FailureError(Exception? Value) : ISparseComponent;

public static partial class GroupedComposition
{
    [Composable]
    public static void Items(ulong[] order)
    {
        foreach (ulong item in order)
        {
            using (Scope.Key(item))
                _ = ReferencedGroups<int>.Header("item", ReferencedGroups<int>.Counter<byte>((int)item));
        }
    }

    [Composable]
    public static void Recovery(bool throwGroup)
    {
        Exception? failure = null;
        if (throwGroup)
        {
            try { ThrowGroup(); }
            catch (InvalidOperationException error) { failure = error; }
        }
        Scope.Write<FailureError>(Scope.Entity) = new(failure);
        var value = Remember(static () => MutableStateOf(20));
        Scope.View(new CounterLabel<int>("tail", value));
    }

    [Composable, Inline]
    private static void ThrowGroup() => _ = ThrowValue();

    [Composable]
    private static int ThrowValue() => throw new InvalidOperationException("group failed");
}
