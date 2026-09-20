using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.Generators;

namespace SomeEngine.Serialization.Tests;

public sealed class ReferencedCodecTests
{
    [Fact]
    public void ReferencedCodecsPreserveObjectViewAndNullableValueBehaviorAndSchemaDependencies()
    {
        ulong first = Exercise("int");
        ulong changed = Exercise("long");
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void AnExplicitValueDeclarationCannotHideMutableReferenceStorage()
    {
        const string source = """
            using SomeEngine.Serialization;
            [BinarySerializable, BinaryValue]
            public sealed partial record Value
            {
                private readonly System.Collections.Generic.List<int> hidden = new();
                public int Number { get; init; }
            }
            """;
        var result = Compile(source);
        Assert.Contains(result.Diagnostics, item => item.Id == "SEBC001"
            && item.GetMessage().Contains("BinaryValue cannot be verified", StringComparison.Ordinal));
    }

    private static ulong Exercise(string numberType)
    {
        string librarySource = $$"""
            using SomeEngine.Serialization;
            [BinarySerializable]
            public sealed partial record ExternalValue
            {
                public {{numberType}} Number { get; init; }
                public string Text { get; init; } = "";
            }
            [BinarySerializable]
            public partial struct ExternalPoint { public int X { get; set; } public string Title { get; set; } }
            """;
        byte[] library = Emit(Compile(librarySource));
        const string consumerSource = """
            using System;
            using System.Buffers;
            using SomeEngine.Serialization;
            [BinarySerializable]
            public partial class Root
            {
                public ExternalValue[] Values { get; set; } = [];
                public ExternalPoint? Point { get; set; }
                public ExternalValue? Optional { get; set; }
            }
            public static class Consumer
            {
                public static ulong Execute()
                {
                    var output = new ArrayBufferWriter<byte>();
                    var value = new Root { Values = [new() { Number = 53, Text = "shared" }], Point = new() { X = 71, Title = "point" },
                        Optional = new() { Number = 91, Text = "optional" } };
                    BinarySerializer.Write(output, value);
                    Root result = BinarySerializer.Deserialize<Root>(output.WrittenSpan);
                    var view = new Root.SpanView(output.WrittenSpan);
                    if (result.Values[0].Number != 53 || result.Point?.X != 71 || result.Point?.Title != "point" || view.GetValuesEncoded().IsEmpty)
                        throw new Exception("Referenced codec behavior changed.");
                    var target = new Root();
                    BinarySerializer.DeserializeInto(new ReadOnlySequence<byte>(output.WrittenMemory), target);
                    if (target.Values[0].Text != "shared") throw new Exception("Referenced object decode failed.");
                    if (result.Optional?.Number != 91 || target.Optional?.Text != "optional" || view.GetOptionalEncoded().IsEmpty)
                        throw new Exception("Referenced nullable object decode failed.");
                    value.Optional = null;
                    output.Clear();
                    BinarySerializer.Write(output, value);
                    result = BinarySerializer.Deserialize<Root>(output.WrittenSpan);
                    BinarySerializer.DeserializeInto(output.WrittenSpan, target);
                    _ = new Root.SpanView(output.WrittenSpan);
                    if (result.Optional is not null || target.Optional is not null)
                        throw new Exception("Referenced nullable object retained a previous value.");
                    if (!typeof(ExternalValue).IsDefined(typeof(BinaryValueAttribute), false)
                        || !typeof(ExternalPoint).IsDefined(typeof(BinaryValueAttribute), false))
                        throw new Exception("The defining assembly did not expose its value proof.");
                    return Root.SchemaFingerprint;
                }
            }
            """;
        byte[] consumer = Emit(Compile(consumerSource, MetadataReference.CreateFromImage(library)));
        var context = new AssemblyLoadContext("ReferencedCodecs-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            using var libraryBytes = new MemoryStream(library);
            context.LoadFromStream(libraryBytes);
            using var consumerBytes = new MemoryStream(consumer);
            Assembly assembly = context.LoadFromStream(consumerBytes);
            return (ulong)assembly.GetType("Consumer")!.GetMethod("Execute")!.Invoke(null, null)!;
        }
        finally { context.Unload(); }
    }

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Compile(string source,
        MetadataReference? dependency = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(BinarySerializableAttribute).Assembly.Location));
        if (dependency is not null) references.Add(dependency);
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("CodecConsumer" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, options)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new BinaryContractGenerator()).WithUpdatedParseOptions(options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (output, diagnostics);
    }

    private static byte[] Emit((Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) result)
    {
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var output = result.Compilation.Emit(stream);
        Assert.True(output.Success, string.Join(Environment.NewLine, output.Diagnostics));
        return stream.ToArray();
    }
}
