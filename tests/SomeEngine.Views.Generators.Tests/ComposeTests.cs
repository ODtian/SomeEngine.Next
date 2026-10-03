using Microsoft.CodeAnalysis;
using SomeEngine.Views.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeTests
{
    [Fact]
    public async Task ReferencedComposeViewsRegisterGeneratedCodecsAndRestoreTheirOutput()
    {
        Assert.True(await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using SomeEngine.ECS.Serialization;
            using System.IO;
            using System.Linq;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    var registry = new SerializationRegistry();
                    ViewSerialization.Register(registry);
                    CheckpointComposition.Number_ViewFunctions.RegisterSerialization(registry);
                    CheckpointLabelFunctions.RegisterSerialization(registry);
                    registry.Register<CheckpointValue, CheckpointValueCodec>(new SerializationTypeKey(
                        new System.Guid("e4b64561-5a8c-4b95-9200-29b54aee4d97"), "ComposeNumber", 1));
                    using var frames = new HeadlessFrameDriver();
                    Entity source = frames.World.CreateEntity(new CheckpointValue(7));
                    Entity root = Views.Spawn(frames.World, Entity.Null, new CheckpointComposition.Number_View(source));
                    frames.Frame();
                    Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    using var checkpoint = new MemoryStream();
                    WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
                    checkpoint.Position = 0;
                    using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));
                    restored.World.Replace(source, new CheckpointValue(13));
                    restored.Frame();
                    return Hierarchy<ElementTree>.GetChildren(restored.World, root).Single() == child &&
                        restored.World.ReadSparse<CheckpointComposition.Number_View>(root).source == source &&
                        restored.World.ReadSparse<CheckpointLabel>(child).Value == 13;
                }
            }
            """));
    }

    [Fact]
    public async Task InternalComposeParameterTypesCanBeStoredAsViewParameters()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Compose;
            public static partial class Composition
            {
                internal record struct Content(int Value);
                [Composable] private static void Label(Content content) { }
                public static object Example() => new Label_View(new Content(3));
            }
            """, true, new ComposeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ReferencedComposeOverloadsSelectTheViewsForTheirParameterTypes()
    {
        bool parameters = await CompilerChecks.Run("""
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel("external"));
                    frames.Frame();
                    Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
                    string[] titles = children.Select(child => frames.World.ReadSparse<Card>(
                        Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).Title).ToArray();
                    return titles.SequenceEqual(new[] { "external", "9" });
                }
            }

            public record struct Panel(string Text) : IView, IComponent
            {
                public BuildTask Build()
                {
                    ReferencedComposition.Amount(Text);
                    ReferencedComposition.DrawNumber(9);
                    return default;
                }
            }
            """);

        Assert.True(parameters);
    }

    [Fact]
    public async Task GenericPrivateComposeMethodsPreserveNamedAndOptionalParameters()
    {
        bool parameters = await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Composition<string>.Panel(8));
                    frames.Frame();
                    Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, child).Single();
                    var view = frames.World.ReadSparse<Composition<string>.Label_View<int>>(child);
                    return view.value == 8 && view.suffix == " default" &&
                        frames.World.ReadSparse<Card>(card).Title == "8 default";
                }
            }

            public static partial class Composition<T> where T : class
            {
                [Composable]
                private static void Label<TValue>(TValue value, string suffix = " default") where TValue : notnull =>
                    Scope.View(new Card(value.ToString() + suffix));

                public record struct Panel(int Value) : IView, IComponent
                {
                    public BuildTask Build()
                    {
                        Label(value: Value);
                        return default;
                    }
                }
            }
            """);

        Assert.True(parameters);
    }

    [Fact]
    public async Task ReferencedComposeMethodsUseTheirAssemblysViewsAndKeepBranchesSeparate()
    {
        bool separate = await CompilerChecks.Run("""
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel(false));
                    frames.Frame();
                    Entity first = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, first).Single();
                    if (frames.World.ReadSparse<ReferencedComposition.Label_View>(first).value != 7 ||
                        frames.World.ReadSparse<Card>(card).Title != "7")
                        return false;
                    frames.World.Replace(card, new CardState(9));
                    frames.World.Replace(root, new Panel(true));
                    frames.Frame();
                    Entity second = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity secondCard = Hierarchy<ElementTree>.GetChildren(frames.World, second).Single();
                    return first != second && !frames.World.IsAlive(first) &&
                        frames.World.ReadSparse<ReferencedComposition.Label_View>(second).value == 7 &&
                        frames.World.Read<CardState>(secondCard).Value == 0;
                }
            }

            public record struct Panel(bool Alternate) : IView, IComponent
            {
                public BuildTask Build()
                {
                    if (Alternate)
                        ReferencedComposition.Label(7);
                    else
                        ReferencedComposition.Label(7);
                    return default;
                }
            }
            """);

        Assert.True(separate);
    }

    [Fact]
    public async Task AnotherGeneratorsComposeCallRequiresSourceInterception()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Compose;
            public static partial class Composition
            {
                [Composable] public static void Label(int value) { }
            }
            """, true, new OtherComposeGenerator().AsSourceGenerator(), new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV008" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV008");
    }

    [Fact]
    public async Task ComposeNameofPreservesTheMethodName()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Compose;
            public static partial class Composition
            {
                [Composable] private static void Label(int value) { }
                public const string LabelName = nameof(Label);
            }
            """, true, new ComposeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task BuildTaskComposeCallsReportChildErrorsAndRecoverTheirContent()
    {
        bool recovered = await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
            using SomeEngine.Views.Tests;
            using SomeEngine.Views.Generators.Tests;
            using SomeEngine.ECS;
            using SomeEngine.ECS.Entities;
            using SomeEngine.ECS.Hierarchy;
            using System;
            using System.Linq;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    using var frames = new HeadlessFrameDriver();
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel("first"));
                    frames.Frame();
                    Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, child).Single();
                    frames.World.Replace(root, new Panel("failed"));
                    frames.Frame();
                    if (frames.World.HasSparse<BuildError>(root) ||
                        frames.World.ReadSparse<BuildError>(child).Exception.Message != "compose failed" ||
                        frames.World.ReadSparse<Card>(card).Title != "first")
                        return false;

                    frames.World.Replace(root, new Panel("recovered"));
                    frames.Frame();
                    return !frames.World.HasSparse<BuildError>(child) &&
                        Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == child &&
                        frames.World.ReadSparse<Card>(card).Title == "recovered";
                }
            }

            public static partial class Composition
            {
                [Composable]
                public static async BuildTask Label(string text)
                {
                    await default(BuildTask);
                    if (text == "failed")
                        throw new InvalidOperationException("compose failed");
                    Scope.View(new Card(text));
                }
            }

            public record struct Panel(string Text) : IView, IComponent
            {
                public async BuildTask Build() => await Composition.Label(Text);
            }
            """);

        Assert.True(recovered);
    }

    [Theory]
    [InlineData("int", "return 1;")]
    [InlineData("System.Threading.Tasks.Task", "return System.Threading.Tasks.Task.CompletedTask;")]
    public async Task ValueProducingComposeDeclarationsFormCallSiteGroups(string result, string body)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views.Compose;
            public static partial class Composition
            {
                [Composable] public static {{result}} Label() { {{body}} }
            }
            """, true, new ComposeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ComposeDeclarationsRequireStaticMethods()
    {
        var diagnostics = await CompilerChecks.Diagnostics("""
            using SomeEngine.Views.Compose;
            public partial class Composition
            {
                [Composable] public void Label(int value) { }
            }
            """, true, new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV006" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV006");
    }

    [Theory]
    [InlineData("System.Action<int> callback = Composition.Label;")]
    [InlineData("var callback = new System.Action<int>(Composition.Label);")]
    [InlineData("delegate*<int, void> callback = &Composition.Label;")]
    public async Task ComposeMethodGroupsDelegatesAndAddressesRequireDirectCalls(string use)
    {
        var diagnostics = await CompilerChecks.Diagnostics($$"""
            using SomeEngine.Views.Compose;
            public static partial class Composition
            {
                [Composable] public static void Label(int value) { }
            }
            public static unsafe class ReferenceChecks
            {
                public static void Check() { {{use}} }
            }
            """, true, new ComposeAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SEV008" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "SEV008");
    }

    [Fact]
    public async Task EqualComposeParametersPreserveChildContentAndChangedParametersUpdateIt()
    {
        bool preserved = await CompilerChecks.Run("""
            using SomeEngine.Views;
            using SomeEngine.Views.Compose;
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
                    Entity root = Views.Spawn(frames.World, Entity.Null, new Panel("first", 0));
                    frames.Frame();
                    Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
                    Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, child).Single();
                    if (frames.World.ReadSparse<Composition.Label_View>(child).text != "first" ||
                        frames.World.ReadSparse<Card>(card).Title != "first old")
                        return false;

                    Composition.Decoration = " new";
                    frames.World.Replace(root, new Panel("first", 1));
                    frames.Frame();
                    if (Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() != child ||
                        frames.World.ReadSparse<Card>(card).Title != "first old" ||
                        frames.World.Read<Refresh>(root).Value != 1)
                        return false;

                    frames.World.Replace(root, new Panel("second", 2));
                    frames.Frame();
                    return Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == child &&
                        frames.World.ReadSparse<Composition.Label_View>(child).text == "second" &&
                        frames.World.ReadSparse<Card>(card).Title == "second new";
                }
            }

            public static partial class Composition
            {
                public static string Decoration = " old";

                [Composable]
                public static void Label(string text) => Scope.View(new Card(text + Decoration));
            }

            public record struct Panel(string Text, int Tick) : IView, IComponent
            {
                public BuildTask Build()
                {
                    Composition.Label(Text);
                    if (Scope.World.Has<Refresh>(Scope.Entity))
                        Scope.World.Replace(Scope.Entity, new Refresh(Tick));
                    else
                        Scope.World.Add(Scope.Entity, new Refresh(Tick));
                    return default;
                }
            }

            public record struct Refresh(int Value) : IComponent;
            """);

        Assert.True(preserved);
    }

    private sealed class OtherComposeGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, static (output, _) => output.AddSource("OtherComposition.g.cs", """
            // <auto-generated/>
            public static class OtherComposition
            {
                public static void Draw() => Composition.Label(3);
            }
            """));
    }
}

public static partial class CheckpointComposition
{
    [Composable]
    public static void Number(SomeEngine.ECS.Entities.Entity source) =>
        SomeEngine.Views.Scope.View(new CheckpointLabel(SomeEngine.Views.Scope.Read<CheckpointValue>(source).Value));
}

public static partial class ReferencedComposition
{
    [Composable]
    public static void Label(int value) => Scope.View(new Card(value.ToString()));

    [Composable]
    public static void Amount(string value) => Scope.View(new Card(value));

    [Composable]
    private static void Amount(int value) => Scope.View(new Card(value.ToString()));

    public static void DrawNumber(int value) => Amount(value);
}
