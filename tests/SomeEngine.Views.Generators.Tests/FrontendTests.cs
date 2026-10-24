using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed class FrontendTests
{
    [Theory]
    [InlineData("public static BuildMode Mode => BuildMode.Rerun;")]
    [InlineData("static BuildMode IView.Mode => BuildMode.Rerun;")]
    public async Task SolidComponentsSuggestReactiveValuesForRerunViewsInAnotherFile(string mode)
    {
        var diagnostics = await CompilerChecks.Diagnostics(
            [
                ("Label.cs", $$"""
                    using SomeEngine.ECS;
                    using SomeEngine.Views;
                    public readonly record struct Label(int Value) : IView, IComponent
                    {
                        {{mode}}
                        public BuildTask Build() => default;
                    }
                    """),
                ("Screens.cs", """
                    using SomeEngine.Views;
                    using SomeEngine.Views.Solid;
                    public static partial class Screens
                    {
                        [Component] public static void Screen(State<int> state) => Scope.View(new Label(state.Value));
                    }
                    """)
            ], new FrontendAnalyzer());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.Equal("Screens.cs", hint.Location.SourceTree!.FilePath);
        Assert.Equal("state.Value", hint.Location.SourceTree.GetText().ToString(hint.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Action<Action> effect = Solid.Effect; effect(static () => { });")]
    [InlineData("Use(Solid.Effect);")]
    public async Task ComposeBodiesReportSolidPrimitiveMethodGroups(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                private static void Use(Action<Action> effect) => effect(static () => { });
                [Composable] public static void Screen() { {{body}} }
            }
            """, true, new FrontendAnalyzer());
        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Effect", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Fact]
    public async Task ExportedSolidComponentsSuggestReactiveDeliveryForStateValues()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                [Component] public static void Screen(State<int> value) => Label(value.Value);
            }
            """, true, new FrontendAnalyzer(), new SolidAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task InsertContentDeliversStateValuesToOtherFrontendComponents()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                [Component] public static void Screen(State<int> value) => Solid.Insert(() => Label(value.Value));
            }
            """, true, new FrontendAnalyzer(), new SolidAnalyzer(), new ComposeAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id is "SEV019" or "SEV020" || value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SolidParameterValuesReadFromStateSuggestReactiveDelivery()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value, int text)
                {
                    Solid.Effect(static () => { });
                    text = value.Value;
                    Label(text);
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SolidComponentsAcceptStateReferencesThroughObjectParameters()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(object value) { }
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    object state = new State<int>(value.Value);
                    Label(state);
                }
            }
            """, true, new FrontendAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id == "SEV020" || value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ComposeBodyReportsSolidShow()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using System;
            using SomeEngine.ECS.Entities;
            using SomeEngine.Views.Compose;
            using static SomeEngine.Views.Solid.Solid;
            public static partial class Screens
            {
                [Composable] public static void Screen() => Show(static () => true, static () => { });
            }
            // Compiler input supplies the public structural primitive described by Solid-Frontend.
            namespace SomeEngine.Views.Solid
            {
                public static class Solid
                {
                    public static Entity Show(Func<bool> when, Action content, Action? fallback = null) => default;
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Compose", error.GetMessage());
        Assert.Contains("Show", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Theory]
    [InlineData("_ = Solid.Memo(static () => 3);")]
    [InlineData("_ = Solid.Untrack(static () => 3);")]
    [InlineData("Solid.Text(static () => \"value\");")]
    [InlineData("Entity.Null.Background(static () => default);")]
    public async Task ComposeBodiesReportSolidCalculationsAndReactiveProperties(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.ECS.Entities;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Screen() { {{body}} }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Theory]
    [InlineData("Label(3);")]
    [InlineData("StateLabel(value);")]
    [InlineData("StateLabel(new State<int>(value.Value));")]
    [InlineData("GetterLabel(() => value.Value);")]
    [InlineData("var copy = value.Value; copy = 3; Label(copy);")]
    [InlineData("Solid.Effect(() => Label(value.Value));")]
    [InlineData("var memo = Solid.Memo(() => value.Value); GetterLabel(memo);")]
    [InlineData("var memo = Solid.Memo(() => value.Value); memo = static () => 3; Label(memo());")]
    [InlineData("Scope.View(new OnceLabel(value.Value));")]
    [InlineData("var copy = false ? value.Value : 3; Label(copy);")]
    public async Task SolidComponentsAcceptConstantsReferencesGettersAndCalculationBodies(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            public record struct OnceLabel(int Value) : IView, ISparseComponent
            {
                public static BuildMode Mode => BuildMode.Once;
                public BuildTask Build() => default;
            }
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                [Composable] public static void StateLabel(State<int> value) { }
                [Composable] public static void GetterLabel(Func<int> value) { }
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    {{body}}
                }
            }
            """, true, new FrontendAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id is "SEV019" or "SEV020" || value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task FrontendComponentsComposeThroughTheirViewBoundaries()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            using static SomeEngine.Views.Compose.Compose;
            public partial record struct Counter : IView, ISparseComponent
            {
                [State] public partial int Count { get; set; }
                public BuildTask Build()
                {
                    Screens.Label(Count);
                    Screens.SolidChild(CountBinding);
                    return default;
                }
            }
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                [Composable] public static void Screen(State<int> value)
                {
                    Scope.View(new Counter());
                    SolidChild(new Binding<int>(value));
                    _ = Remember(static () => 3);
                    Solid.Text("value");
                    void Child() => Solid.Effect(static () => { });
                    Child();
                }
                public static void SolidChild(Binding<int> value)
                {
                    Solid.Effect(() => _ = value.Value);
                    Label(3);
                }
            }
            """, true, new FrontendAnalyzer(), new ComposeAnalyzer(), new StateAnalyzer(), new ViewAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id is "SEV019" or "SEV020" || value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Action run = () => { var memo = Solid.Memo(() => value.Value); Label(memo()); };")]
    [InlineData("void Child() { var memo = Solid.Memo(() => value.Value); Label(memo()); } Child();")]
    public async Task NestedSolidComponentsSuggestReactiveDeliveryForTheirMemoReads(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value) { {{body}} }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SolidComponentsAcceptTheResultOfWritingAConstantStateValue()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    Label(value.Value = 3);
                }
            }
            """, true, new FrontendAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id == "SEV020" || value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("var copy = value.Value; if (change) copy = 3; Label(copy);")]
    [InlineData("var copy = value.Value; while (change) { copy = 3; change = false; } Label(copy);")]
    [InlineData("var copy = 0; copy += value.Value; Label(copy);")]
    public async Task ReactiveValuesRemainVisibleAcrossBranchesAndUpdates(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value, bool change)
                {
                    Solid.Effect(static () => { });
                    {{body}}
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SwiftPropertyNamesRemainAvailableInComposeBodies()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.ECS.Components;
            public partial record struct Counter : IView, ISparseComponent
            {
                [State] public partial int Count { get; set; }
                public BuildTask Build() => default;
            }
            public static partial class Screens
            {
                [Composable] public static void Screen(Counter counter) => _ = nameof(counter.Count);
            }
            """, true, new FrontendAnalyzer());

        Assert.DoesNotContain(diagnostics, value => value.Id == "SEV019" || value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("CounterFunctions.Counter(value.Value);")]
    [InlineData("Scope.View(new Counter(value.Value));")]
    [InlineData("Scope.View(new Counter(value.Value), 7);")]
    [InlineData("Scope.View(key: 7, view: new Counter(value.Value));")]
    [InlineData("Scope.View(new Counter(0) { Value = value.Value });")]
    [InlineData("var counter = new Counter(value.Value); Scope.View(counter);")]
    public async Task SolidStateValuesPassedToSwiftViewsSuggestReactiveDelivery(string call)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            public partial record struct Counter(int Value) : IView, ISparseComponent
            {
                [State] public partial int Count { get; set; }
                public BuildTask Build() => default;
            }
            public static class Screens
            {
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    {{call}}
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.Contains("Insert", hint.GetMessage());
        Assert.Contains("State", hint.GetMessage());
        Assert.Equal("value.Value", hint.Location.SourceTree!.GetText().ToString(hint.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("var first = value.Value; var second = first + 1; Label(second);")]
    [InlineData("int first; first = value.Value; Label(first);")]
    [InlineData("var memo = Solid.Memo(() => value.Value); Label(memo());")]
    [InlineData("var memo = Solid.Memo(() => value.Value); var read = memo; var result = read() + 1; Label(result);")]
    public async Task SolidLocalValuesAndMemoReadsSuggestReactiveDelivery(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    {{body}}
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.Contains("Insert", hint.GetMessage());
        Assert.Contains("State", hint.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("value.Value")]
    [InlineData("value.Value + 1")]
    public async Task SolidStateValuesPassedToComposeComponentsSuggestReactiveDelivery(string value)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable] public static void Label(int value) { }
                public static void Screen(State<int> value)
                {
                    Solid.Effect(static () => { });
                    Label({{value}});
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic hint = Assert.Single(diagnostics, value => value.Id == "SEV020");
        Assert.Equal(DiagnosticSeverity.Info, hint.Severity);
        Assert.Contains("Insert", hint.GetMessage());
        Assert.Contains("State", hint.GetMessage());
        Assert.Equal(value, hint.Location.SourceTree!.GetText().ToString(hint.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Action run = () => { Solid.Effect(static () => { }); _ = Remember(static () => 3); };")]
    [InlineData("void Child() { Solid.Effect(static () => { }); _ = Remember(static () => 3); } Child();")]
    [InlineData("Action run = delegate { Solid.Effect(static () => { }); _ = Remember(static () => 3); };")]
    public async Task NestedSolidFunctionsReportTheirOwnRememberCall(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            using static SomeEngine.Views.Compose.Compose;
            public static partial class Screens
            {
                [Composable] public static void Screen() { {{declaration}} }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Remember", error.GetMessage());
        Assert.Contains("Solid", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Fact]
    public async Task ComposableLambdaReportsItsOwnSolidPrimitive()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using System;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static class Screens
            {
                public static void Screen()
                {
                    Action run = [Composable] static () => Solid.Effect(static () => { });
                }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Compose", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Theory]
    [InlineData("public BuildTask Build()")]
    [InlineData("BuildTask IView.Build()")]
    public async Task SwiftStateBuildReportsSolidMemo(string build)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            public partial record struct Counter : IView, ISparseComponent
            {
                [State] public partial int Count { get; set; }
                {{build}} { _ = Solid.Memo(static () => 3); return default; }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("SwiftUI", error.GetMessage());
        Assert.Contains("Memo", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Theory]
    [InlineData("[Composable] public static void Screen(Counter counter) => _ = counter.Count;")]
    [InlineData("public static void Screen(Counter counter) { Solid.Effect(static () => { }); _ = counter.Count; }")]
    public async Task OtherFrontendBodiesReportSwiftStateAccess(string declaration)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.SwiftUI;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            public partial record struct Counter : IView, ISparseComponent
            {
                [State] public partial int Count { get; set; }
                public BuildTask Build() => default;
            }
            public static partial class Screens { {{declaration}} }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Count", error.GetMessage());
        Assert.Contains("SwiftUI", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Theory]
    [InlineData("_ = Remember(static () => 3); Solid.Effect(static () => { });")]
    [InlineData("Solid.Effect(static () => { }); _ = Remember(static () => 3);")]
    public async Task SolidBodyReportsRememberRegardlessOfCallOrder(string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views.Solid;
            using static SomeEngine.Views.Compose.Compose;
            public static class Screens
            {
                public static void Screen() { {{body}} }
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Remember", error.GetMessage());
        Assert.Contains("Solid", error.GetMessage());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }

    [Fact]
    public async Task ComposeBodyReportsSolidEffect()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            public static partial class Screens
            {
                [Composable]
                public static void Screen() => Solid.Effect(static () => { });
            }
            """, true, new FrontendAnalyzer());

        Diagnostic error = Assert.Single(diagnostics, value => value.Id == "SEV019");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("Compose", error.GetMessage());
        Assert.Contains("Solid", error.GetMessage());
        Assert.Equal("Solid.Effect(static () => { })", error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error && value.Id != "SEV019");
    }
}
