using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGen;
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
public static class Consumer
{
    public static bool Execute()
    {
        var registry = new SerializationRegistry();
        global::GameSerializationModule.RegisterAll(registry);
        var value = new RootGenerated { Value = 42 };
        using var stream = new System.IO.MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in value, registry);
        stream.Position = 0;
        return WorldSerializer.ReadDurableComponent<RootGenerated>(stream, registry).Value == 42;
    }
}
""");

        Assert.True((bool)Execute(result, "SomeEngine.ECS.SourceGen.TestInput.Consumer")!);
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
    public GeneratedNested Nested;
}
public static class Consumer
{
    public static bool Execute()
    {
        var registry = new SerializationRegistry();
        global::GameSerializationModule.RegisterAll(registry);
        var world = new SomeEngine.ECS.World();
        var target = world.CreateEntity();
        var entity = world.CreateEntity(new GeneratedPosition { X = 17, Nested = new GeneratedNested { Target = target } });
        using var stream = new System.IO.MemoryStream();
        WorldSerializer.WriteCheckpointWorld(stream, world, registry);
        stream.Position = 0;
        var loaded = WorldSerializer.ReadCheckpointWorld(stream, registry);
        var value = loaded.Read<GeneratedPosition>(entity);
        return value.X == 17 && value.Nested.Target == target && loaded.IsAlive(target);
    }
}
""");

        Assert.True((bool)Execute(result, "SomeEngine.ECS.SourceGen.TestInput.Consumer")!);
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER002" && diagnostic.Location.IsInSource);
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER003" && diagnostic.Location.IsInSource);
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER006" && diagnostic.Location.IsInSource);
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
public static class Consumer
{
    public static bool Execute()
    {
        var registry = new SerializationRegistry();
        global::GameSerializationModule.RegisterAll(registry);
        var value = new StableShape { Value = WideMode.Maximum };
        using var stream = new System.IO.MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in value, registry);
        stream.Position = 0;
        return WorldSerializer.ReadDurableComponent<StableShape>(stream, registry).Value == WideMode.Maximum;
    }
}
""");

        Assert.True((bool)Execute(result, "SomeEngine.ECS.SourceGen.TestInput.Consumer")!);
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
public static class Consumer
{
    public static bool Execute()
    {
        var registry = new SerializationRegistry();
        global::GameSerializationModule.RegisterAll(registry);
        var value = new PackedPosition { X = int.MinValue, Y = 123.5f };
        using var stream = new System.IO.MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in value, registry);
        stream.Position = 0;
        var loaded = WorldSerializer.ReadDurableComponent<PackedPosition>(stream, registry);
        return loaded.X == int.MinValue && loaded.Y == 123.5f;
    }
}
""");

        Assert.True((bool)Execute(result, "SomeEngine.ECS.SourceGen.TestInput.Consumer")!);
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

        Assert.Equal(ReadFingerprint(forward), ReadFingerprint(reversed));
    }

    private static ulong GeneratedFingerprint(string source) => ReadFingerprint(RunGenerator(source));

    private static ulong ReadFingerprint(GeneratorRunResult result)
    {
        const string consumer = """
public static class FingerprintConsumer
{
    public static ulong Execute()
    {
        var registry = new SomeEngine.ECS.Serialization.SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        return registry.Entries[0].TypeKey.SchemaFingerprint;
    }
}
""";
        Compilation compilation = result.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            consumer, new CSharpParseOptions(LanguageVersion.Preview)));
        return (ulong)Execute(result with { Compilation = compilation }, "FingerprintConsumer")!;
    }

    private static object? Execute(GeneratorRunResult result, string type)
    {
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        return assembly.GetType(type, throwOnError: true)!.GetMethod("Execute")!.Invoke(null, null);
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
            assemblyName: "SerializationGeneratorTests_" + Guid.NewGuid().ToString("N"),
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

        return new GeneratorRunResult(diagnostics, outputCompilation);
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
        Compilation Compilation);
}
