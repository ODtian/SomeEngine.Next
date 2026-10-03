using SomeEngine.ECS.Components;
using SomeEngine.Views.Compose;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeGroupTests
{
    [Fact]
    public async Task ReferencedGenericGroupsPreserveStateWithOverloadsAndOptionalArguments()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System.Linq;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Screen(true));
                    frames.Frame();
                    Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    if (children.Length != 2)
                        return false;
                    var first = frames.World.ReadSparse<CounterLabel<int>>(children[0]);
                    var second = frames.World.ReadSparse<CounterLabel<int>>(children[1]);
                    first.Value.Value = 11;
                    second.Value.Value = 22;
                    frames.Frame();
                    frames.World.Replace(root, new Screen(false));
                    frames.Frame();
                    if (Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() != children[1] ||
                        frames.World.ReadSparse<CounterNumber<int>>(children[1]).Value != 22)
                        return false;
                    frames.World.Replace(root, new Screen(true));
                    frames.Frame();
                    Entity[] returned = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    return returned.Length == 2 && returned[1] == children[1] &&
                        frames.World.ReadSparse<CounterNumber<int>>(returned[0]).Value == 11 &&
                        frames.World.ReadSparse<CounterNumber<int>>(returned[1]).Value == 22 &&
                        frames.World.ReadSparse<CounterLabel<int>>(returned[1]).Text == "tail" &&
                        !frames.World.HasSparse<BuildError>(root);
                }
            }

            public record struct Screen(bool IncludeFirst) : IView, IComponent
            {
                public BuildTask Build()
                {
                    if (IncludeFirst)
                        _ = ReferencedGroups<int>.Header("x", ReferencedGroups<int>.Counter<byte>(10, callKey: 1));
                    _ = ReferencedGroups<int>.Header(ReferencedGroups<int>.Counter("tail"),
                        ReferencedGroups<int>.Counter<byte>(20));
                    return default;
                }
            }
            """));
    }

    [Fact]
    public async Task ValueProducingComposablesKeepTheirRememberedStateInExpressions()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.ECS.Components;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System;
            using System.Linq;
            using static SomeEngine.Views.Compose.Compose;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition.Screen_View(0));
                    frames.Frame();
                    Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    if (frames.World.ReadSparse<Label>(label).Value != 5)
                        return false;
                    frames.World.ReadSparse<ChangeCounter>(root).Change(7);
                    frames.Frame();
                    if (frames.World.ReadSparse<Label>(label).Value != 12)
                        return false;
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(1));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == label &&
                        frames.World.ReadSparse<Label>(label).Value == 12 &&
                        !frames.World.HasSparse<BuildError>(root);
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Screen(int tick) => Scope.View(new Label(5 + RememberCounter()));

                [Composable]
                private static int RememberCounter()
                {
                    var count = Remember(static () => MutableStateOf(0));
                    Scope.Write<ChangeCounter>(Scope.Entity) = new ChangeCounter(next => count.Value = next);
                    return count.Value;
                }
            }

            public record struct ChangeCounter(Action<int> Change) : ISparseComponent;
            public record struct Label(int Value) : IView, ISparseComponent
            {
                public BuildTask Build() => default;
            }
            """));
    }

    [Fact]
    public async Task ConditionalInlineCallsKeepRememberedValuesAndStatefulNodesSeparate()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Components;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System;
            using System.Linq;
            using static SomeEngine.Views.Compose.Compose;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition.Screen_View(true));
                    frames.Frame();
                    Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    if (children.Length != 2 || frames.World.HasSparse<BuildError>(root))
                        return false;
                    Entity tail = children[1];
                    var first = frames.World.ReadSparse<Label>(children[0]);
                    var second = frames.World.ReadSparse<Label>(tail);
                    if (first.Text != "x" || first.Value != 10 || second.Text != "y" || second.Value != 20)
                        return false;
                    first.Change(11);
                    second.Change(22);
                    frames.World.Replace(tail, new LabelState(77));
                    frames.Frame();
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(false));
                    frames.Frame();
                    Entity remaining = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    if (remaining != tail || frames.World.ReadSparse<Label>(remaining).Value != 22 ||
                        frames.World.Read<LabelState>(remaining).Value != 77 || frames.World.HasSparse<BuildError>(root))
                        return false;
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(true));
                    frames.Frame();
                    children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    return children.Length == 2 && children[1] == tail &&
                        frames.World.ReadSparse<Label>(children[0]).Value == 11 &&
                        frames.World.ReadSparse<Label>(tail).Value == 22 &&
                        frames.World.Read<LabelState>(tail).Value == 77 &&
                        !frames.World.HasSparse<BuildError>(root);
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Screen(bool includeFirst)
                {
                    if (includeFirst) Header("x"); Header("y");
                }

                [Composable, Inline]
                private static void Header(string text)
                {
                    var value = Remember(() => MutableStateOf(text == "x" ? 10 : 20));
                    Scope.View(new Label(text, value.Value, next => value.Value = next));
                }
            }

            public record struct Label(string Text, int Value, Action<int> Change) : IView, ISparseComponent
            {
                public BuildTask Build()
                {
                    if (!Scope.World.Has<LabelState>(Scope.Entity))
                    {
                        Scope.World.Add(Scope.Entity, new LabelState(0));
                        Scope.World.AddTag<LocalState>(Scope.Entity);
                    }
                    return default;
                }
            }

            public record struct LabelState(int Value) : IComponent;
            """));
    }
}

public static partial class ReferencedGroups<T> where T : struct
{
    [Composable]
    public static State<T> Counter<TNumber>(T initial, int callKey = 7) where TNumber : unmanaged =>
        Remember(() => MutableStateOf(initial));

    [Composable]
    public static string Counter(string initial) => Remember(() => initial);

    [Composable, Inline]
    public static BuildTask Header(string text, State<T> value)
    {
        Scope.View(new CounterLabel<T>(text, value));
        return default;
    }
}

public readonly record struct CounterNumber<T>(T Value) : ISparseComponent;

public readonly record struct CounterLabel<T>(string Text, State<T> Value) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Write<CounterNumber<T>>(Scope.Entity) = new(Value.Value);
        return default;
    }
}
