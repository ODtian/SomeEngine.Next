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
