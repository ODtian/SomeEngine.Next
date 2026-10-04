using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed partial class RememberTests
{
    [Fact]
    public void RememberUsesTheDevelopmentCheckForStatefulSiblingCountChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RememberedScreens.Children_View(2));
        frames.Frame();

        frames.World.ReplaceSparse(root, new RememberedScreens.Children_View(3));
        frames.Frame();

#if DEBUG
        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(2, Hierarchy<ElementTree>.GetChildren(frames.World, root).Count());
#else
        Assert.Equal(3, Hierarchy<ElementTree>.GetChildren(frames.World, root).Count());
#endif
    }

    [Fact]
    public void RememberUsesTheDevelopmentCheckOrShippingIdentityWhenInterceptionIsBypassed()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new UninterceptedMemory(3));
        frames.Frame();

#if DEBUG
        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
#else
        frames.World.Replace(root, new UninterceptedMemory(7));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadSparse<ComposeText>(root).Value);
#endif
    }
}

public readonly record struct UninterceptedMemory(int Initial) : IView, IComponent
{
    public BuildTask Build()
    {
#pragma warning disable SEV008
        Func<Func<int>, int> remember = SomeEngine.Views.Compose.Compose.Remember<int>;
#pragma warning restore SEV008
        int initial = Initial;
        Scope.Write<ComposeText>(Scope.Entity) = new(remember(() => initial).ToString());
        return default;
    }
}

public static partial class RememberedScreens
{
    [Composable]
    public static void Children(int count)
    {
        for (int index = 0; index < count; index++)
            Child();
    }

    [Composable]
    private static void Child()
    {
        var value = Remember(static () => MutableStateOf(3));
        Scope.Write<ComposeText>(Scope.Entity) = new(value.Value.ToString());
    }
}
