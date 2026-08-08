using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGen;
using System.Text.RegularExpressions;
using Xunit;

namespace SomeEngine.ECS.SourceGen.Tests;

public class SerializationGeneratorTests
{
    [Fact]
    public void EmitsCodecForCanonicalRootOnlyComponent()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Serialization;

[SerializableComponent("01010101-0101-0101-0101-010101010101")]
public partial struct RootGenerated : global::SomeEngine.ECS.IComponent
{
    public int Value;
}
""");

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = Assert.Single(
            result.Sources,
            source => source.HintName == "SomeEngine.ECS.Serialization.Module.g.cs")
            .SourceText
            .ToString();
        Assert.Contains(
            "registry.RegisterCanonical<global::SomeEngine.ECS.SourceGen.TestInput.RootGenerated",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsRegisterAllCodecsAndPatchers()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

[SerializableComponent("11111111-1111-1111-1111-111111111111")]
public partial struct GeneratedNested : SomeEngine.ECS.IComponent
{
    public Entity Target;
}

[SerializableComponent("22222222-2222-2222-2222-222222222222")]
public partial struct GeneratedPosition : SomeEngine.ECS.IComponent
{
    public int X;
    public string Name;
    public GeneratedNested Nested;
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(result.Sources, source => source.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        Assert.Contains("GameSerializationModule", generated);
        Assert.Contains("registry.Register<global::SomeEngine.ECS.SourceGen.TestInput.GeneratedPosition", generated);
        Assert.Contains("IReferencePatcher<global::SomeEngine.ECS.SourceGen.TestInput.GeneratedPosition>", generated);
        Assert.Contains("writer.WriteString(value.Name)", generated);
        Assert.Contains("writer.WriteEntity(value.Target)", generated);
    }

    [Fact]
    public void ReportsDuplicateStableIds()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;

[SerializableComponent("33333333-3333-3333-3333-333333333333")]
public partial struct FirstGenerated : SomeEngine.ECS.IComponent
{
    public int X;
}

[SerializableComponent("33333333-3333-3333-3333-333333333333")]
public partial struct SecondGenerated : SomeEngine.ECS.IComponent
{
    public int X;
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER002");
    }

    [Fact]
    public void ReportsUnsupportedFields()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;

[SerializableComponent("44444444-4444-4444-4444-444444444444")]
public partial struct InvalidGenerated : SomeEngine.ECS.IComponent
{
    public object Value;
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER003");
    }

    [Fact]
    public void ReportsDuplicateSerializedFieldIdentityIncludingDefaultNameCollision()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC")]
public struct InvalidGenerated : SomeEngine.ECS.IComponent
{
    public int Position;
    [SerializedField("Position")]
    public int RenamedPosition;
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER006");
    }

    [Fact]
    public void SchemaFingerprint_ChangesForSameSizeFieldReorder()
    {
        ulong first = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("55555555-5555-5555-5555-555555555555")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    public int Left;
    public int Right;
}
""");
        ulong reordered = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("55555555-5555-5555-5555-555555555555")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    public int Right;
    public int Left;
}
""");

        Assert.NotEqual(first, reordered);
    }

    [Fact]
    public void SchemaFingerprint_ChangesForSameSizeFieldTypeChange()
    {
        ulong integer = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("66666666-6666-6666-6666-666666666666")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    public int Value;
}
""");
        ulong floatingPoint = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("66666666-6666-6666-6666-666666666666")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    public float Value;
}
""");

        Assert.NotEqual(integer, floatingPoint);
    }

    [Fact]
    public void StableSerializedFieldIdentity_AllowsSourceFieldRename()
    {
        ulong beforeRename = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("88888888-8888-8888-8888-888888888888")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    [SerializedField("position-x")]
    public int X;
}
""");
        ulong afterRename = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("88888888-8888-8888-8888-888888888888")]
public struct StableShape : SomeEngine.ECS.IComponent
{
    [SerializedField("position-x")]
    public int RenamedX;
}
""");

        Assert.Equal(beforeRename, afterRename);
    }

    [Fact]
    public void CodecVersion_ChangesSchemaFingerprint()
    {
        ulong versionOne = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("99999999-9999-9999-9999-999999999999", CodecVersion = 1)]
public struct StableShape : SomeEngine.ECS.IComponent { public int Value; }
""");
        ulong versionTwo = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[SerializableComponent("99999999-9999-9999-9999-999999999999", CodecVersion = 2)]
public struct StableShape : SomeEngine.ECS.IComponent { public int Value; }
""");

        Assert.NotEqual(versionOne, versionTwo);
    }

    [Fact]
    public void EnumMemberValue_ChangesSchemaFingerprint()
    {
        ulong first = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
public enum Mode : uint { Active = 1 }
[SerializableComponent("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
public struct StableShape : SomeEngine.ECS.IComponent { public Mode Value; }
""");
        ulong changed = GeneratedFingerprint("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
public enum Mode : uint { Active = 2 }
[SerializableComponent("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
public struct StableShape : SomeEngine.ECS.IComponent { public Mode Value; }
""");

        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void UnsignedEnum_UsesUInt64CanonicalWireEncoding()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
public enum WideMode : ulong { Maximum = 18446744073709551615UL }
[SerializableComponent("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB")]
public struct StableShape : SomeEngine.ECS.IComponent { public WideMode Value; }
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = Assert.Single(
            result.Sources,
            item => item.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        Assert.Contains("writer.WriteUInt64", generated);
        Assert.Contains("reader.ReadUInt64", generated);
    }

    [Fact]
    public void ExplicitPackedPrimitiveLayout_EmitsProvenRawCanonicalFastPath()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SerializableComponent("77777777-7777-7777-7777-777777777777")]
public struct PackedPosition : SomeEngine.ECS.IComponent
{
    public int X;
    public float Y;
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = Assert.Single(
            result.Sources,
            source => source.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        Assert.Contains("registry.RegisterCanonical<global::SomeEngine.ECS.SourceGen.TestInput.PackedPosition", generated);
        Assert.Contains(", 8, 0x", generated);
        Assert.DoesNotContain(", 8, 0x0000000000000000ul);", generated);
        Assert.Contains("ICanonicalComponentCodec<global::SomeEngine.ECS.SourceGen.TestInput.PackedPosition>", generated);
    }

    [Fact]
    public void PackedPrimitiveLayoutAcrossPartialDeclarations_DoesNotEmitRawProof()
    {
        const string attributedPart = """
namespace SomeEngine.ECS.SourceGen.TestInput;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SerializableComponent("71717171-7171-7171-7171-717171717171")]
public partial struct PartialPacked : SomeEngine.ECS.IComponent
{
    public int Z;
}
""";
        const string secondPart = """
namespace SomeEngine.ECS.SourceGen.TestInput;
public partial struct PartialPacked
{
    public float A;
}
""";

        GeneratorRunResult forward = RunGenerator(attributedPart, secondPart);
        GeneratorRunResult reversed = RunGenerator(secondPart, attributedPart);
        Assert.DoesNotContain(
            forward.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            reversed.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        string forwardSource = Assert.Single(
            forward.Sources,
            source => source.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        string reversedSource = Assert.Single(
            reversed.Sources,
            source => source.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        Assert.Contains(", -1, 0x0000000000000000ul);", forwardSource);
        Assert.Equal(forwardSource, reversedSource);
    }

    private static ulong GeneratedFingerprint(string source)
    {
        var result = RunGenerator(source);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = Assert.Single(
            result.Sources,
            item => item.HintName == "SomeEngine.ECS.Serialization.Module.g.cs").SourceText.ToString();
        Match match = Regex.Match(generated, @"0x([0-9A-F]{16})ul");
        Assert.True(match.Success, generated);
        return Convert.ToUInt64(match.Groups[1].Value, 16);
    }

    private static GeneratorRunResult RunGenerator(params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree[] syntaxTrees = sources
            .Select((source, index) => CSharpSyntaxTree.ParseText(
                source,
                parseOptions,
                path: $"Input{index}.cs"))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            assemblyName: "SerializationGeneratorTests",
            syntaxTrees: syntaxTrees,
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new SerializationGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        var runResult = driver.GetRunResult();
        var diagnostics = generatorDiagnostics
            .Concat(runResult.Results.SelectMany(result => result.Diagnostics))
            .Concat(outputCompilation.GetDiagnostics())
            .ToArray();

        return new GeneratorRunResult(diagnostics, runResult.Results.SelectMany(result => result.GeneratedSources).ToArray());
    }

    private static MetadataReference[] GetReferences()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(SomeEngine.ECS.World).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SomeEngine.ECS.Serialization.SerializationRegistry).Assembly.Location));
        return references.ToArray();
    }

    private sealed record GeneratorRunResult(
        Diagnostic[] Diagnostics,
        GeneratedSourceResult[] Sources);
}
