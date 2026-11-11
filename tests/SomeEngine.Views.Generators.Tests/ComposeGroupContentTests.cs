namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeGroupContentTests
{
    [Fact]
    public async Task ValueProducingGroupsDeclareIndependentContentFunctions()
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

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    State<int>[] values = [new(10), new(20)];
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition.Screen_View(values));
                    frames.Frame();
                    Entity[] content = ElementChildren.Read(frames.World, root).ToArray();
                    if (content.Length != 2 || content.Any(child =>
                        ElementChildren.Read(frames.World, child).Length != 1))
                        return false;
                    Entity[] labels = content.Select(child =>
                        ElementChildren.Read(frames.World, child).Single()).ToArray();
                    values[0].Value = 11;
                    frames.Frame();
                    return content.SequenceEqual(ElementChildren.Read(frames.World, root)) &&
                        frames.World.ReadComponentData<Label>(labels[0]).Value == 11 &&
                        frames.World.ReadComponentData<Label>(labels[1]).Value == 20 &&
                        frames.World.ReadComponentData<DisplayedCount>(root).Value == 3 &&
                        !frames.World.HasSparse<BuildError>(root);
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Screen(State<int>[] values) =>
                    Scope.Write<DisplayedCount>(Scope.Entity) = new(1 + Rows(values,
                        static value => Scope.View(new Label(value.Value))));

                [Composable]
                private static int Rows(State<int>[] values, [Composable] Action<State<int>> row)
                {
                    foreach (State<int> value in values)
                        row(value);
                    return values.Length;
                }
            }

            public record struct DisplayedCount(int Value) : ISparseComponent;
            public record struct Label(int Value) : IView, ISparseComponent
            {
                public BuildTask Build() => default;
            }
            """));
    }

    [Fact]
    public async Task InlineGroupsKeepRememberedValuesSeparateInsideCallerContent()
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition.Screen_View(true));
                    frames.Frame();
                    Entity[] panels = ElementChildren.Read(frames.World, root).ToArray();
                    Entity first = ElementChildren.Read(frames.World, panels[0]).Single();
                    Entity second = ElementChildren.Read(frames.World, panels[1]).Single();
                    frames.World.ReadComponentData<Label>(first).Change(11);
                    frames.World.ReadComponentData<Label>(second).Change(22);
                    frames.Frame();
                    frames.World.SetSparse(root, new Composition.Screen_View(false));
                    frames.Frame();
                    if (ElementChildren.Read(frames.World, root).Single() != panels[1] ||
                        ElementChildren.Read(frames.World, panels[1]).Single() != second ||
                        frames.World.ReadComponentData<Label>(second).Value != 22)
                        return false;
                    frames.World.SetSparse(root, new Composition.Screen_View(true));
                    frames.Frame();
                    panels = ElementChildren.Read(frames.World, root).ToArray();
                    first = ElementChildren.Read(frames.World, panels[0]).Single();
                    return frames.World.ReadComponentData<Label>(first).Value == 11 &&
                        frames.World.ReadComponentData<Label>(second).Value == 22 &&
                        !frames.World.HasSparse<BuildError>(root);
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Screen(bool includeFirst)
                {
                    if (includeFirst) Header(10);
                    Header(20);
                }

                [Composable, Inline]
                private static void Header(int initial)
                {
                    using (Scope.View(new Panel()).Content())
                    {
                        var value = Remember(() => MutableStateOf(initial));
                        Scope.View(new Label(value.Value, next => value.Value = next));
                    }
                }
            }

            public record struct Panel() : IView, ISparseComponent
            {
                public static ChildSource Children => ChildSource.Caller;
                public BuildTask Build() => default;
            }

            public record struct Label(int Value, Action<int> Change) : IView, ISparseComponent
            {
                public BuildTask Build() => default;
            }
            """));
    }
}
