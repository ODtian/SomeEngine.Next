using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ContentFunctionTests
{
    [Fact]
    public async Task ConditionalAndRepeatedContentCallsProduceIndependentChildViews()
    {
        bool correct = await CompilerChecks.Run("""
            using System;
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null,
                        new Composition.Rows_View<int>(new[] { 2, 5 }, true,
                            static item => Scope.View(new Card(item.ToString()))));
                    frames.Frame();
                    Entity[] content = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    string[] titles = content.Select(child => frames.World.ReadSparse<Card>(
                        Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).Title).ToArray();
                    if (!titles.SequenceEqual(new[] { "0", "2", "5" }))
                        return false;

                    frames.World.ReplaceSparse(root, frames.World.ReadSparse<Composition.Rows_View<int>>(root) with
                        { header = false, items = new[] { 7, 8 } });
                    frames.Frame();
                    Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    return rows.SequenceEqual(content.Skip(1)) && !frames.World.IsAlive(content[0]) &&
                        rows.Select(child => frames.World.ReadSparse<Card>(
                            Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).Title)
                        .SequenceEqual(new[] { "7", "8" });
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Rows<T>(T[] items, bool header, [Composable] Action<T> row)
                {
                    if (header)
                        row(default!);
                    foreach (T item in items)
                        row(item);
                }
            }
            """);

        Assert.True(correct);
    }

    [Fact]
    public async Task ContentDelegatesWithEqualTargetsRefreshContentWhenTheirReferencesChange()
    {
        bool refreshed = await CompilerChecks.Run("""
            using System;
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel(new Action<int>(Composition.Draw), 0));
                    frames.Frame();
                    Entity rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, rows).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, content).Single();
                    if (frames.World.ReadSparse<Card>(card).Title != "3 old")
                        return false;

                    Composition.Decoration = " new";
                    frames.World.Replace(root, new Panel(new Action<int>(Composition.Draw), 1));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == rows &&
                        Hierarchy<ElementTree>.GetChildren(frames.World, rows).Single() == content &&
                        frames.World.ReadSparse<Card>(card).Title == "3 new";
                }
            }

            public static partial class Composition
            {
                public static string Decoration = " old";
                public static void Draw(int item) => Scope.View(new Card(item + Decoration));

                [Composable]
                public static void Rows([Composable] Action<int> row) => row(3);
            }

            public record struct Panel(Action<int> Row, int Tick) : IView, IComponent
            {
                public BuildTask Build() { Composition.Rows(Row); return default; }
            }
            """);

        Assert.True(refreshed);
    }

    [Fact]
    public async Task StaticContentLambdasPreserveChildContentWhenParentParametersChange()
    {
        bool preserved = await CompilerChecks.Run("""
            using System;
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel(3, 0));
                    frames.Frame();
                    Entity rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, rows).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, content).Single();
                    Composition.Decoration = " new";
                    frames.World.Replace(root, new Panel(3, 1));
                    frames.Frame();
                    if (frames.World.ReadSparse<Card>(card).Title != "3 old" ||
                        frames.World.Read<Refresh>(rows).Value != 1)
                        return false;

                    frames.World.Replace(root, new Panel(4, 2));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, rows).Single() == content &&
                        frames.World.ReadSparse<Card>(card).Title == "4 new";
                }
            }

            public static partial class Composition
            {
                public static string Decoration = " old";

                [Composable]
                public static void Rows(int item, int tick, [Composable] Action<int> row)
                {
                    if (Scope.World.Has<Refresh>(Scope.Entity))
                        Scope.World.Replace(Scope.Entity, new Refresh(tick));
                    else
                        Scope.World.Add(Scope.Entity, new Refresh(tick));
                    row(item);
                }
            }

            public record struct Refresh(int Value) : IComponent;
            public record struct Panel(int Item, int Tick) : IView, IComponent
            {
                public BuildTask Build()
                {
                    Composition.Rows(Item, Tick, static item => Scope.View(new Card(item + Composition.Decoration)));
                    return default;
                }
            }
            """);

        Assert.True(preserved);
    }

    [Theory]
    [InlineData("[Composable] public static void Screen(int value) => Use(() => value.ToString());")]
    [InlineData("public record struct Screen(int Value) : IView, IComponent { public BuildTask Build() { int value = Value; Use(() => value.ToString()); return default; } }")]
    public async Task CapturedLambdaArgumentsReportAllocationInComposeAndSwiftUiBodies(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.ECS;
            public static partial class Composition
            {
                public static void Use(Func<string> content) { }
                {{declaration}}
            }
            """, true, new ComposeAnalyzer());

        Diagnostic diagnostic = Assert.Single(diagnostics, value => value.Id == "SEV013");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("调用方每次重跑都会分配", diagnostic.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("[Composable] public static void Screen() => Use(static () => \"static\");")]
    [InlineData("[Composable] public static void Screen() => Use(() => \"constant\");")]
    [InlineData("public static void Screen(int value) => Use(() => value.ToString());")]
    [InlineData("public record struct Screen(int Value) : IView, IComponent { public static BuildMode Mode => BuildMode.Once; public BuildTask Build() { int value = Value; Use(() => value.ToString()); return default; } }")]
    [InlineData("[Composable] public static void Screen() => Accept(item => () => item.ToString());")]
    public async Task AllocationHintsApplyToCapturedArgumentsInRerunningViewBodies(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.ECS;
            public static partial class Composition
            {
                public static void Use(Func<string> content) { }
                public static void Accept(Func<int, Func<string>> content) { }
                {{declaration}}
            }
            """, true, new ComposeAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id == "SEV013" || value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ReferencedGenericContentFunctionsKeepTheirDependenciesOnEachChild()
    {
        bool refreshed = await CompilerChecks.Run("""
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    var first = new State<int>(2);
                    var second = new State<int>(5);
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel(new[] { first, second }));
                    frames.Frame();
                    Entity rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity list = Hierarchy<ElementTree>.GetChildren(frames.World, rows).Single();
                    Entity[] content = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
                    Entity[] cards = content.Select(child => Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).ToArray();
                    Composition.Decoration = " new";
                    first.Value = 7;
                    frames.Frame();
                    return content.SequenceEqual(Hierarchy<ElementTree>.GetChildren(frames.World, list)) &&
                        frames.World.ReadSparse<Card>(cards[0]).Title == "7 new" &&
                        frames.World.ReadSparse<Card>(cards[1]).Title == "5 old";
                }
            }

            public static class Composition { public static string Decoration = " old"; }
            public record struct Panel(State<int>[] Items) : IView, IComponent
            {
                public BuildTask Build()
                {
                    ReferencedContent.Rows(Items, static state => Scope.View(new Card(state.Value + Composition.Decoration)));
                    return default;
                }
            }
            """);

        Assert.True(refreshed);
    }

    [Fact]
    public async Task BuildTaskContentFunctionsUseTheirOwnChildScopeAndRecoverErrors()
    {
        bool resumed = await CompilerChecks.Run("""
            using System;
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null,
                        new Composition.Rows_View("first", static async title =>
                        {
                            await default(BuildTask);
                            if (title == "failed")
                                throw new InvalidOperationException("content failed");
                            Scope.View(new Card(title));
                        }));
                    frames.Frame();
                    Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, content).Single();
                    var view = frames.World.ReadSparse<Composition.Rows_View>(root);
                    frames.World.ReplaceSparse(root, view with { title = "failed" });
                    frames.Frame();
                    if (frames.World.HasSparse<BuildError>(root) ||
                        frames.World.ReadSparse<BuildError>(content).Exception is not System.InvalidOperationException ||
                        frames.World.ReadSparse<Card>(card).Title != "first")
                        return false;

                    frames.World.ReplaceSparse(root, view with { title = "restored" });
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == content &&
                        frames.World.ReadSparse<Card>(card).Title == "restored" &&
                        !frames.World.HasSparse<BuildError>(root) && !frames.World.HasSparse<BuildError>(content);
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static async BuildTask Rows(string title, [Composable] Func<string, BuildTask> row)
                {
                    await default(BuildTask);
                    await row(title);
                }
            }
            """);

        Assert.True(resumed);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("Func<int>")]
    [InlineData("RefContent")]
    public async Task ContentParametersRequireDelegateResultsAndValueArguments(string type)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views.Compose;
            public delegate void RefContent(ref int value);
            public static partial class Composition
            {
                [Composable] public static void Rows([Composable] {{type}} row) { }
            }
            """, true, new ComposeAnalyzer());

        Assert.Contains(diagnostics, value => value.Id == "SEV014" && value.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV014");
    }

    [Fact]
    public async Task CustomContentDelegatesPreserveNamespaceAliasesAndNamedOptionalArguments()
    {
        bool rendered = await CompilerChecks.Run("""
            global using Caption = System.String;
            using System.Linq;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null,
                        new Screens.Composition<int>.Rows_View(9,
                            static (content, suffix) => Scope.View(new Card(content + suffix))));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root)
                        .Select(child => frames.World.ReadSparse<Card>(
                            Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).Title)
                        .SequenceEqual(new[] { "9!", "9?" });
                }
            }

            namespace Screens
            {
                using Title = Caption;
                public static partial class Composition<T> where T : notnull
                {
                    public delegate void DrawRow(T content, Title suffix = "?");

                    [Composable]
                    public static void Rows(T value, [Composable] DrawRow row)
                    {
                        T content = value;
                        row(suffix: "!", content: content);
                        row(content);
                    }
                }
            }
            """);

        Assert.True(rendered);
    }
}

public static partial class ReferencedContent
{
    [SomeEngine.Views.Compose.Composable]
    public static void Rows<T>(T[] items, [SomeEngine.Views.Compose.Composable] Action<T> row) => List(items, row);

    [SomeEngine.Views.Compose.Composable]
    private static void List<T>(T[] items, [SomeEngine.Views.Compose.Composable] Action<T> row)
    {
        foreach (T item in items)
            row.Invoke(item);
    }
}
