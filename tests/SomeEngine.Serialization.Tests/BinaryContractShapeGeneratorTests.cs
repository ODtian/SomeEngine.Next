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

    [Fact]
    public void PartialRecordClassAndStructProduceCompilableGeneratedContracts()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinarySerializable]
            public partial record class RecordClass
            {
                public string Name { get; set; } = string.Empty;
            }

            [BinarySerializable]
            public partial record struct RecordStruct
            {
                public int Value { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
        Assert.True((bool)Execute(result, """
            byte[] bytes = new byte[256];
            if (!BinarySerializer.TryWrite(bytes, new RecordClass { Name = "record" }, out int count)) return false;
            if (BinarySerializer.Deserialize<RecordClass>(bytes.AsSpan(0, count)).Name != "record") return false;
            if (!BinarySerializer.TryWrite(bytes, new RecordStruct { Value = 73 }, out count)) return false;
            return BinarySerializer.Deserialize<RecordStruct>(bytes.AsSpan(0, count)).Value == 73;
            """)!);
    }

    [Fact]
    public void ConcreteContractInheritanceIncludesBaseMembers()
    {
        const string source = """
            using SomeEngine.Serialization;

            public class ContractBase
            {
                public int BaseValue { get; set; }
            }

            [BinarySerializable]
            public partial class DerivedContract : ContractBase
            {
                public int OwnValue { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);
        Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
        Assert.True((bool)Execute(result, """
            var output = new System.Buffers.ArrayBufferWriter<byte>();
            BinarySerializer.Write(output, new DerivedContract { BaseValue = 17, OwnValue = 23 });
            DerivedContract value = BinarySerializer.Deserialize<DerivedContract>(output.WrittenSpan);
            return value.BaseValue == 17 && value.OwnValue == 23;
            """)!);

    }

    [Fact]
    public void ViewGetterForTypeMemberDoesNotHideObjectGetType()
    {
        const string source = """
            using SomeEngine.Serialization;

            [BinarySerializable]
            public partial class TypeMemberContract
            {
                public int Type { get; set; }
            }
            """;

        GeneratorRun result = RunGenerator(source);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, static item => item.Id == "CS0108");
        Assert.Equal(17, (int)Execute(result, """
            byte[] bytes = new byte[256];
            if (!BinarySerializer.TryWrite(bytes, new TypeMemberContract { Type = 17 }, out int count)) throw new Exception();
            var view = new TypeMemberContract.SpanView(bytes.AsSpan(0, count));
            return view.GetTypeValue();
            """)!);
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

            [BinarySerializable]
            public sealed partial class Leaf
            {
                public int Number { get; set; }
            }

            [BinarySerializable(LogicalName = "Tests.NullableFingerprint.Holder")]
            public partial class Holder
            {
                public {{memberType}} Value { get; set; } = default!;
            }
            """;
        GeneratorRun result = RunGenerator(source);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return (ulong)Execute(result, "return Holder.SchemaFingerprint;")!;
    }

    [Fact]
    public void PartialFileReorganizationKeepsEncodingAndSchema()
    {
        const string first = """
            using SomeEngine.Serialization;
            [BinarySerializable(LogicalName = "Tests.PartialFields")]
            public partial class Value
            {
                public int Z { get; set; }
            }
            """;
        const string second = """
            public partial class Value
            {
                public int A { get; set; }
            }
            """;
        GeneratorRun original = RunGenerator([("a.cs", first), ("z.cs", second)]);
        GeneratorRun moved = RunGenerator([("folder/z.cs", first), ("a.cs", second)]);
        const string consume = """
            var output = new System.Buffers.ArrayBufferWriter<byte>();
            BinarySerializer.Write(output, new Value { A = 17, Z = 23 });
            return new object[] { output.WrittenSpan.ToArray(), Value.SchemaFingerprint };
            """;
        object[] originalResult = (object[])Execute(original, consume)!;
        object[] movedResult = (object[])Execute(moved, consume)!;
        Assert.Equal((byte[])originalResult[0], (byte[])movedResult[0]);
        Assert.Equal(originalResult[1], movedResult[1]);
    }

    [Fact]
    public void ExplicitFieldOrderControlsBytesAndSchema()
    {
        const string source = """
            using SomeEngine.Serialization;
            [BinarySerializable(LogicalName = "Tests.ExplicitFields")]
            [BinaryFields(nameof(Z), nameof(A))]
            public partial class Value
            {
                public int A { get; set; }
                public int Z { get; set; }
            }
            """;
        const string consume = """
            var output = new System.Buffers.ArrayBufferWriter<byte>();
            BinarySerializer.Write(output, new Value { A = 17, Z = 23 });
            var reader = new BinaryDataReader(output.WrittenSpan);
            Value result = BinarySerializer.Deserialize<Value>(output.WrittenSpan);
            return new object[] { reader.ReadInt32(), Value.SchemaFingerprint, result.A == 17 && result.Z == 23 };
            """;
        object[] first = (object[])Execute(RunGenerator(source), consume)!;
        object[] reordered = (object[])Execute(RunGenerator(source.Replace("nameof(Z), nameof(A)", "nameof(A), nameof(Z)")), consume)!;
        Assert.Equal(23, first[0]);
        Assert.Equal(17, reordered[0]);
        Assert.NotEqual(first[1], reordered[1]);
        Assert.Equal(true, first[2]);
        Assert.Equal(true, reordered[2]);
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

    [Fact]
    public void MovingAnEnumWithItsDeclaredBinaryNamePreservesBytesAndSchema()
    {
        const string original = """
            using SomeEngine.Serialization;
            namespace Before { public enum Mode : byte { None = 0, Enabled = 1 } }
            [BinarySerializable]
            public partial class Value { public Before.Mode Mode { get; set; } }
            """;
        string moved = original.Replace("namespace Before { public enum", "namespace After { [BinaryName(\"Before.Mode\")] public enum")
            .Replace("Before.Mode Mode", "After.Mode Mode");
        const string body = """
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            BinarySerializer.Write(bytes, new Value());
            return new object[] { Value.SchemaFingerprint, bytes.WrittenSpan.ToArray() };
            """;
        object[] first = (object[])Execute(RunGenerator(original), body)!;
        object[] second = (object[])Execute(RunGenerator(moved), body)!;
        Assert.Equal(first[0], second[0]);
        Assert.Equal((byte[])first[1], (byte[])second[1]);
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
