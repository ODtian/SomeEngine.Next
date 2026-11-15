using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.Generators;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryContractShapeGeneratorTests
{
    public static TheoryData<string, string> NullableShapePairs => new()
    {
        { "string", "string?" },
        { "Leaf", "Leaf?" },
        { "int[]", "int[]?" },
        { "System.Collections.Generic.List<string>", "System.Collections.Generic.List<string?>" },
        { "System.Collections.Generic.Dictionary<string, string>", "System.Collections.Generic.Dictionary<string, string?>" },
        { "System.Collections.Generic.IDictionary<int, string>", "System.Collections.Generic.IDictionary<int, string>?" },
        { "ProbeEnum", "ProbeEnum?" },
    };

    [Fact]
    public void UnsupportedRuntimePolymorphicAndProcessLocalShapesFailAtCompileTime()
    {
        const string source = """
            #nullable enable
            using System;
            using System.Threading.Tasks;
            using SomeEngine.Serialization;

            public interface IUndeclaredPolymorphism { }

            [BinarySerializable]
            public unsafe partial class InvalidShapes
            {
                public object ObjectValue = new();
                public dynamic DynamicValue = new();
                public Action DelegateValue = static () => { };
                public Task TaskValue = Task.CompletedTask;
                public IUndeclaredPolymorphism InterfaceValue = null!;
                public int* PointerValue;
            }
            """;

        GeneratorRun result = RunGenerator(source, allowUnsafe: true);
        Diagnostic[] diagnostics = result.Diagnostics.Where(static item => item.Id == "SEBC003").ToArray();

        Assert.Equal(6, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.True(diagnostic.Location.IsInSource));
    }

    [Fact]
    public void DictionaryWithFloatingPointKeyFailsAtCompileTime()
    {
        const string source = """
            using System.Collections.Generic;
            using SomeEngine.Serialization;

            [BinarySerializable]
            public partial class InvalidMap
            {
                public Dictionary<double, int> Values { get; set; } = new();
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, static item => item.Id == "SEBC003");
        Assert.True(diagnostic.Location.IsInSource);

    }

    [Fact]
    public void UnionCasesMustBeSealedConcreteContracts()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinaryUnion(typeof(OpenCase))]
            public interface IMessage { }

            [BinarySerializable]
            [BinaryUnionCase(1)]
            public partial class OpenCase : IMessage { }

            [BinarySerializable]
            public partial class Envelope
            {
                public IMessage Value { get; set; } = new OpenCase();
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, static item => item.Id == "SEBC003");
        Assert.True(diagnostic.Location.IsInSource);

    }

    [Theory]
    [InlineData("\"A\"", "missing")]
    [InlineData("\"A\", \"A\", \"Z\"", "repeated")]
    [InlineData("\"A\", \"Z\", \"Absent\"", "not serialized")]
    [InlineData("null", "null")]
    public void ExplicitFieldOrderReportsIncompleteOrAmbiguousDeclarations(string fields, string message)
    {
        string source = $$"""
            using SomeEngine.Serialization;
            [BinarySerializable]
            [BinaryFields({{fields}})]
            public partial class Value
            {
                public int A { get; set; }
                public int Z { get; set; }
            }
            """;
        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Id == "SEBC012");
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Contains(message, diagnostic.GetMessage());
    }

    private static GeneratorRun RunGenerator(string source, bool allowUnsafe = false)
        => RunGenerator([("ContractShapeInput.cs", source)], allowUnsafe);

    private static GeneratorRun RunGenerator((string Path, string Source)[] sources, bool allowUnsafe = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree[] syntaxTrees = sources.Select(item => CSharpSyntaxTree.ParseText(item.Source, parseOptions, path: item.Path)).ToArray();
        var compilation = CSharpCompilation.Create(
            assemblyName: "BinaryContractShapeGeneratorTests_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: syntaxTrees,
            references: GetReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: allowUnsafe,
                nullableContextOptions: NullableContextOptions.Enable));
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
        references.Add(MetadataReference.CreateFromFile(typeof(BinarySerializableAttribute).Assembly.Location));
        return references.ToArray();
    }

    private sealed record GeneratorRun(
        Diagnostic[] Diagnostics,
        Compilation Compilation);
}
