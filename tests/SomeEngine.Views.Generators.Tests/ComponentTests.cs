using Microsoft.CodeAnalysis;
using SomeEngine.Views.Solid;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComponentTests
{
    [Fact]
    public async Task ComposableLambdasDeclareSolidViewsThroughTheirOwnMethodAttribute()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
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
                    var value = new State<int>(3);
                    Action content = [Composable] () => Components.Label(value.Value);
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Host(content));
                    frames.Frame();
                    Entity component = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, component).Single();
                    value.Value = 7;
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == component &&
                        frames.World.ReadSparse<Card>(card).Title == "3";
                }
            }
            public static partial class Components
            {
                [Component] public static void Label(int value) => Scope.View(new Card(value.ToString()));
            }
            public record struct Host(Action Content) : IView, ISparseComponent
            {
                public BuildTask Build() { Content(); return default; }
            }
            """));
    }

    [Fact]
    public async Task SolidFunctionsUseOrdinaryDelegatesAndNameofWithinTheirOwnBodies()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Solid;
            public static partial class Components
            {
                [Component] public static void Inner() { }
                [Component] public static void Outer()
                {
                    System.Action content = Inner;
                    content();
                    string name = nameof(Inner);
                }
            }
            """, true, new SolidAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SolidExportsFromOtherGeneratedBuildBodiesReportTheirSourceCallRequirement()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Solid;
            public static partial class Components
            {
                [Component] public static void Counter() { }
            }
            """, true, new OtherComponentGenerator().AsSourceGenerator(), new SolidAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV018" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV018");
    }

    [Fact]
    public async Task ComposeContentMethodsDeclareSolidChildrenWithOnceContent()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Solid;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Host(3));
                    frames.Frame();
                    Entity row = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity component = Hierarchy<ElementTree>.GetChildren(frames.World, row).First();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, component).Single();
                    frames.World.ReplaceSparse(root, new Host(7));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, row).First() == component &&
                        Hierarchy<ElementTree>.GetChildren(frames.World, component).Single() == card &&
                        frames.World.ReadSparse<Card>(card).Title == "3";
                }
            }
            public static partial class Components
            {
                [Composable]
                public static void Row(int value, [Composable] Action content)
                {
                    Label(value);
                    content();
                }
                [Component]
                public static void Label(int value) => Scope.View(new Card(value.ToString()));
            }
            public record struct Host(int Value) : IView, ISparseComponent
            {
                public BuildTask Build()
                {
                    Components.Row(Value, static () => Scope.View(new Card("content")));
                    return default;
                }
            }
            """));
    }

    [Theory]
    [InlineData("public async BuildTask Build()")]
    [InlineData("async BuildTask IView.Build()")]
    public async Task AsyncSolidExportsUseOnceViewsInImplicitAndExplicitBuildBodies(string build)
    {
        Assert.True(await CompilerChecks.Run($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Solid;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS.Components;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System.Linq;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Host(3));
                    frames.Frame();
                    Entity component = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, component).Single();
                    frames.World.ReplaceSparse(root, new Host(7));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == component &&
                        Hierarchy<ElementTree>.GetChildren(frames.World, component).Single() == card &&
                        frames.World.ReadSparse<Card>(card).Title == "3";
                }
            }
            public static partial class Components
            {
                [Component]
                public static async BuildTask Label<T>(T value, string suffix = "") where T : struct
                {
                    await default(BuildTask);
                    Scope.View(new Card(value + suffix));
                }
            }
            public record struct Host(int Value) : IView, ISparseComponent
            {
                {{build}} => await Components.Label<int>(value: Value);
            }
            """));
    }

    [Fact]
    public async Task ReferencedSolidOverloadsPreserveTheirOnceContentAcrossHostRefreshes()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS.Components;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System.Linq;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Host(3, "first"));
                    frames.Frame();
                    Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    Entity[] cards = children.Select(child => Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).ToArray();
                    if (!cards.Select(card => frames.World.ReadSparse<Card>(card).Title).SequenceEqual(new[] { "3 number", "first" }))
                        return false;
                    frames.World.ReplaceSparse(root, new Host(7, "latest"));
                    frames.Frame();
                    return children.SequenceEqual(Hierarchy<ElementTree>.GetChildren(frames.World, root)) &&
                        cards.Select(card => frames.World.ReadSparse<Card>(card).Title).SequenceEqual(new[] { "3 number", "first" });
                }
            }

            public record struct Host(int Value, string Text) : IView, ISparseComponent
            {
                public BuildTask Build()
                {
                    ReferencedSolid.Amount<int>(Value, suffix: " number");
                    ReferencedSolid.Amount(Text);
                    return default;
                }
            }
            """));
    }

    [Theory]
    [InlineData("System.Action content = Components.Counter;")]
    [InlineData("var content = new System.Action(Components.Counter);")]
    [InlineData("delegate*<void> content = &Components.Counter;")]
    public async Task SolidExportsInBuildBodiesRequireDirectSourceCalls(string use)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views;
            using SomeEngine.Views.Solid;
            using SomeEngine.ECS.Components;
            public static partial class Components
            {
                [Component] public static void Counter() { }
            }
            public record struct Host : IView, ISparseComponent
            {
                public unsafe BuildTask Build() { {{use}} return default; }
            }
            """, true, new SolidAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV018" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV018");
    }

    [Theory]
    [InlineData("int", "", "return 1;")]
    [InlineData("System.Threading.Tasks.Task", "", "return System.Threading.Tasks.Task.CompletedTask;")]
    [InlineData("void", "ref int value", "")]
    public async Task ExportedSolidSignaturesDescribeViewContent(string result, string parameters, string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views.Solid;
            public static partial class Components
            {
                [Component] public static {{result}} Counter({{parameters}}) { {{body}} }
            }
            """, true, new SolidAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV017" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV017");
    }

    [Fact]
    public async Task ExportedSolidComponentsRequireStaticMethods()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Solid;
            public partial class Components
            {
                [Component] public void Counter() { }
            }
            """, true, new SolidAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV016" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV016");
    }

    private sealed class OtherComponentGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, static (output, _) => output.AddSource("OtherHost.g.cs", """
                // <auto-generated/>
                public record struct OtherHost : SomeEngine.Views.IView, SomeEngine.ECS.Components.ISparseComponent
                {
                    public SomeEngine.Views.BuildTask Build()
                    {
                        Components.Counter();
                        return default;
                    }
                }
                """));
    }
}

public static partial class ReferencedSolid
{
    [Component]
    public static void Amount<T>(T value, string suffix = "") where T : struct, ISpanFormattable =>
        Scope.View(new Card(value.ToString(null, null) + suffix));

    [Component]
    public static void Amount(string value) => Scope.View(new Card(value));
}
