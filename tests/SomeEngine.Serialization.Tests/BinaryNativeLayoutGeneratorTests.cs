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
        Assert.True((bool)Execute(result, """
            byte[] bytes = new byte[256];
            NativeVertex[] values = [new NativeVertex { Position = new NativeVector2 { X = 1.5f, Y = -2.5f }, Color = 123, Flags = 7 }];
            var writer = new BinaryDataWriter(bytes);
            NativeBlock.Write<NativeVertex>(ref writer, values, NativeVertex.NativeLayoutProof);
            var reader = new BinaryDataReader(bytes.AsSpan(0, writer.WrittenCount));
            var read = NativeBlock.Read<NativeVertex>(ref reader, NativeVertex.NativeLayoutProof);
            return read.Length == 1 && read[0].Position.X == 1.5f && read[0].Position.Y == -2.5f && read[0].Color == 123 && read[0].Flags == 7;
            """)!);
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
        Assert.True(diagnostic.Location.IsInSource);
    }

    private static object? Execute(GeneratorRun result, string body)
    {
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        string consumer = "using System; using SomeEngine.Serialization; public static class Consumer { public static object Execute() { " + body + " } }";
        Compilation compilation = result.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            consumer, new CSharpParseOptions(LanguageVersion.Preview)));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        return assembly.GetType("Consumer")!.GetMethod("Execute")!.Invoke(null, null);
    }

    private static GeneratorRun RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "NativeLayoutInput.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "BinaryNativeLayoutGeneratorTests_" + Guid.NewGuid().ToString("N"),
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
        return new GeneratorRun(diagnostics, outputCompilation);
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
        Compilation Compilation);
}
