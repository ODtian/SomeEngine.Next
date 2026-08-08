using System.Collections.Immutable;
using System.Text.RegularExpressions;
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

    [Theory]
    [MemberData(nameof(NullableShapePairs))]
    public void NullableAnnotationsParticipateInSchemaFingerprint(string nonNullableType, string nullableType)
    {
        ulong nonNullable = GenerateFingerprint(nonNullableType);
        ulong nullable = GenerateFingerprint(nullableType);

        Assert.NotEqual(nonNullable, nullable);
    }

    [Fact]
    public void UnsupportedRuntimePolymorphicAndProcessLocalShapesFailAtCompileTime()
    {
        const string source = """
            #nullable enable
            using System;
            using System.Threading.Tasks;
            using SomeEngine.Serialization;

            public interface IUndeclaredPolymorphism { }

            [BinaryContract]
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
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("object requires runtime polymorphism", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("dynamic requires runtime polymorphism", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("delegates", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("Task", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("explicit closed [BinaryUnion]", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.GetMessage().Contains("process-local", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryWithFloatingPointKeyFailsAtCompileTime()
    {
        const string source = """
            using System.Collections.Generic;
            using SomeEngine.Serialization;

            [BinaryContract]
            public partial class InvalidMap
            {
                public Dictionary<double, int> Values { get; set; } = new();
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, static item => item.Id == "SEBC003");

        Assert.Contains("canonical total ordering", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnionCasesMustBeSealedConcreteContracts()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinaryUnion(typeof(OpenCase))]
            public interface IMessage { }

            [BinaryContract]
            [BinaryUnionCase(1)]
            public partial class OpenCase : IMessage { }

            [BinaryContract]
            public partial class Envelope
            {
                public IMessage Value { get; set; } = new OpenCase();
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, static item => item.Id == "SEBC003");

        Assert.Contains("sealed, concrete", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void PartialRecordClassAndStructProduceCompilableGeneratedContracts()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinaryContract]
            public partial record class RecordClass
            {
                public string Name { get; set; } = string.Empty;
            }

            [BinaryContract]
            public partial record struct RecordStruct
            {
                public int Value { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Sources, static item => item.HintName.EndsWith("RecordClass.BinaryContract.g.cs", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static item => item.HintName.EndsWith("RecordStruct.BinaryContract.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcreteContractInheritanceFailsInsteadOfSilentlyDroppingBaseMembers()
    {
        const string source = """
            using SomeEngine.Serialization;

            public class ContractBase
            {
                public int BaseValue { get; set; }
            }

            [BinaryContract]
            public partial class DerivedContract : ContractBase
            {
                public int OwnValue { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, static item => item.Id == "SEBC001");

        Assert.Contains("inherited storage cannot be silently omitted", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ViewGetterForTypeMemberDoesNotHideObjectGetType()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinaryContract]
            public partial class TypeMemberContract
            {
                public int Type { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, static item => item.Id == "CS0108");
        string generated = Assert.Single(
            result.Sources,
            static item => item.HintName.EndsWith("TypeMemberContract.BinaryContract.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        Assert.Contains("GetTypeValue()", generated, StringComparison.Ordinal);
        Assert.DoesNotContain(" GetType()", generated, StringComparison.Ordinal);
    }

    private static ulong GenerateFingerprint(string memberType)
    {
        string source = $$"""
            #nullable enable
            using SomeEngine.Serialization;

            public enum ProbeEnum : short
            {
                First = 1,
                Second = 2,
            }

            [BinaryContract]
            public sealed partial class Leaf
            {
                public int Number { get; set; }
            }

            [BinaryContract(LogicalName = "Tests.NullableFingerprint.Holder")]
            public partial class Holder
            {
                public {{memberType}} Value { get; set; } = default!;
            }
            """;
        GeneratorRun result = RunGenerator(source);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = Assert.Single(
            result.Sources,
            static item => item.HintName.EndsWith("Holder.BinaryContract.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        Match match = Regex.Match(generated, @"SchemaFingerprint => 0x(?<value>[0-9A-F]{16})UL", RegexOptions.CultureInvariant);
        Assert.True(match.Success, generated);
        return Convert.ToUInt64(match.Groups["value"].Value, 16);
    }

    private static GeneratorRun RunGenerator(string source, bool allowUnsafe = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "ContractShapeInput.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "BinaryContractShapeGeneratorTests",
            syntaxTrees: [syntaxTree],
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
