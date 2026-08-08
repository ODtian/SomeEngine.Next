using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.Generators;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryNativeLayoutGeneratorTests
{
    [Fact]
    public void PaddingFreeRecursiveLayoutCompilesToGeneratedProofs()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using SomeEngine.Serialization;

            [BinaryContract]
            [BinaryNativeLayout("Tests.NativeVector2.v1")]
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public partial struct NativeVector2
            {
                public float X;
                public float Y;
            }

            [BinaryContract]
            [BinaryNativeLayout("Tests.NativeVertex.v1")]
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public partial struct NativeVertex
            {
                public NativeVector2 Position;
                public uint Color;
                public uint Flags;
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string vectorSource = Assert.Single(
            result.Sources,
            generated => generated.HintName.EndsWith("NativeVector2.BinaryContract.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        string vertexSource = Assert.Single(
            result.Sources,
            generated => generated.HintName.EndsWith("NativeVertex.BinaryContract.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        Assert.Contains("NativeLayoutProof", vectorSource, StringComparison.Ordinal);
        Assert.Contains(".CreateGenerated(", vectorSource, StringComparison.Ordinal);
        Assert.Contains("NativeLayoutProof", vertexSource, StringComparison.Ordinal);
        Assert.Contains(".CreateGenerated(", vertexSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutWithAbiPaddingProducesNativeProofDiagnostic()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using SomeEngine.Serialization;

            [BinaryContract]
            [BinaryNativeLayout("Tests.Padded.v1")]
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public partial struct Padded
            {
                public byte Tag;
                public int Value;
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Id == "SEBC010");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("padding", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        string generated = Assert.Single(
            result.Sources,
            item => item.HintName.EndsWith("Padded.BinaryContract.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        Assert.DoesNotContain("NativeLayoutProof", generated, StringComparison.Ordinal);
    }

    private static GeneratorRun RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "NativeLayoutInput.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "BinaryNativeLayoutGeneratorTests",
            syntaxTrees: [syntaxTree],
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new BinaryContractGenerator().AsSourceGenerator())
            .WithUpdatedParseOptions(parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out ImmutableArray<Diagnostic> generatorDiagnostics);
        GeneratorDriverRunResult runResult = driver.GetRunResult();
        Diagnostic[] diagnostics = generatorDiagnostics
            .Concat(runResult.Results.SelectMany(static result => result.Diagnostics))
            .Concat(outputCompilation.GetDiagnostics())
            .GroupBy(static diagnostic => diagnostic.ToString(), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        GeneratedSourceResult[] sources = runResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .ToArray();
        return new GeneratorRun(diagnostics, sources);
    }

    private static MetadataReference[] GetReferences()
    {
        List<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(BinaryContractAttribute).Assembly.Location));
        return references.ToArray();
    }

    private sealed record GeneratorRun(
        Diagnostic[] Diagnostics,
        GeneratedSourceResult[] Sources);
}
