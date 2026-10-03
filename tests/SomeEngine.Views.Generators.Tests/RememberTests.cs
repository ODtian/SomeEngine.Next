using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators.Tests;

public sealed partial class RememberTests
{
    [Theory]
    [InlineData("Func<Func<int>, int> remember = Remember<int>;")]
    [InlineData("var remember = new Func<Func<int>, int>(Remember<int>);")]
    public async Task RememberMethodGroupsRequireDirectCalls(string use)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views.Compose;
            using static SomeEngine.Views.Compose.Compose;
            public static partial class Composition
            {
                [Composable] public static void Example() { {{use}} }
            }
            """, true, new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV008" &&
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
            diagnostic.Id != "SEV008");
    }

    [Fact]
    public async Task AnotherGeneratorsRememberCallRequiresSourceInterception()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            public static partial class Composition { }
            """, true, new OtherRememberGenerator().AsSourceGenerator(), new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV008" &&
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
            diagnostic.Id != "SEV008");
    }

    [Theory]
    [InlineData("public static void Example() { _ = Remember(static () => 3); }")]
    [InlineData("[Composable] public static void Example() { Action action = () => { _ = Remember(static () => 3); }; }")]
    public async Task RememberRequiresTheCallingMethodToBeComposable(string method)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using System;
            using SomeEngine.Views.Compose;
            using static SomeEngine.Views.Compose.Compose;
            public static partial class Composition
            {
                {{method}}
            }
            """, true, new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV009" &&
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
            diagnostic.Id != "SEV009");
    }

    [Fact]
    public async Task ConditionalRememberValuesReturnWithTheirStateAndKeepFollowingValuesSeparate()
    {
        bool preserved = await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.ECS.Components;
            using SomeEngine.ECS.Entities;
            using System;
            using static SomeEngine.Views.Compose.Compose;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition.Screen_View(false));
                    frames.Frame();
                    var first = frames.World.ReadSparse<Display>(root);
                    if (first.Value != 20 || first.Tail != 30)
                        return false;
                    first.Change(23);
                    first.ChangeTail(37);
                    frames.Frame();
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(true));
                    frames.Frame();
                    var second = frames.World.ReadSparse<Display>(root);
                    if (second.Value != 10 || second.Tail != 37)
                        return false;
                    second.Change(11);
                    frames.Frame();
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(false));
                    frames.Frame();
                    if (frames.World.ReadSparse<Display>(root).Value != 23)
                        return false;
                    frames.World.ReplaceSparse(root, new Composition.Screen_View(true));
                    frames.Frame();
                    var returned = frames.World.ReadSparse<Display>(root);
                    return returned.Value == 11 && returned.Tail == 37;
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Screen(bool alternate)
                {
                    State<int> value;
                    if (alternate)
                        value = Remember(static () => MutableStateOf(10));
                    else
                        value = Remember(static () => MutableStateOf(20));
                    var tail = Remember(static () => MutableStateOf(30));
                    Scope.Write<Display>(Scope.Entity) = new Display(value.Value, tail.Value,
                        next => value.Value = next, next => tail.Value = next);
                }
            }

            public record struct Display(int Value, int Tail, Action<int> Change, Action<int> ChangeTail) : ISparseComponent;
            """);

        Assert.True(preserved);
    }

    private sealed class OtherRememberGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, static (output, _) => output.AddSource("OtherRemember.g.cs", """
                // <auto-generated/>
                using SomeEngine.Views.Compose;
                using static SomeEngine.Views.Compose.Compose;
                public static partial class Composition
                {
                    [Composable] public static void Example() { _ = Remember(static () => 3); }
                }
                """));
    }
}
