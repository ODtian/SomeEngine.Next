using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGenerators;

namespace SomeEngine.ECS.SourceGenerators.Tests;

public sealed class EcsTypeGeneratorMetadataTests
{
    [Fact]
    public void PrivateStorageRetainsManagedFieldRootsAndRegistersAotClassification()
    {
        string generated = RunGenerator("""
namespace @event;
public static class Components
{
    private struct Leaf { internal nuint Value { get; set; } }
    private struct Stored : SomeEngine.ECS.IComponent { internal Leaf Value { get; set; } }
}
""");

        Assert.Contains("ModuleInitializer", generated);
        Assert.Contains("DynamicallyAccessedMemberTypes.PublicFields", generated);
        Assert.Contains("DynamicallyAccessedMemberTypes.NonPublicFields", generated);
        AssertRoot(generated, "event.Components.Stored", "MetadataConsumer");
        AssertRoot(generated, "event.Components.Leaf", "MetadataConsumer");
        AssertStorageRegistration(generated, "event.Components+Stored, MetadataConsumer", 0, isAliasFree: false);
        Assert.DoesNotContain("RegisterJobStorage<", generated);
    }

    [Fact]
    public void PrivateGenericStorageKeepsFieldRootsAndRegistersEachAotClassification()
    {
        MetadataReference reference = ReferencedFields();
        string generated = RunGenerator("""
namespace GenericPrivate;
public static class Components
{
    private struct FirstLeaf { internal nuint Value { get; set; } }
    private struct SecondLeaf { internal long Value { get; set; } }
    private struct Stored<T> : SomeEngine.ECS.IComponent where T : unmanaged
    {
        internal Referenced.Outer<T>.Inner Value { get; set; }
    }
    private static class Outer<T> where T : unmanaged
    {
        internal struct Nested<U> : SomeEngine.ECS.IComponent where U : unmanaged
        {
            internal T First { get; set; }
            internal U Second { get; set; }
        }
    }
    private struct IndirectStorage<T> : SomeEngine.ECS.IComponent { internal T Value { get; set; } }
    private static void Use<T>() where T : struct { }
    private static void UseArray<T>() => Use<IndirectStorage<T[]>>();
    public static void Touch()
    {
        Use<Stored<FirstLeaf>>();
        Use<Stored<SecondLeaf>>();
        Use<Outer<SecondLeaf>.Nested<FirstLeaf>>();
    }
}
""", reference);

        AssertRoot(generated, "GenericPrivate.Components.Stored`1", "MetadataConsumer");
        AssertRoot(generated, "Referenced.Outer`1.Inner", "ReferencedFields");
        AssertRoot(generated, "GenericPrivate.Components.FirstLeaf", "MetadataConsumer");
        AssertRoot(generated, "GenericPrivate.Components.SecondLeaf", "MetadataConsumer");
        AssertRoot(generated, "GenericPrivate.Components.Outer`1.Nested`1", "MetadataConsumer");
        AssertStorageRegistration(generated,
            "GenericPrivate.Components+Outer`1+Nested`1[[GenericPrivate.Components+SecondLeaf, MetadataConsumer],[GenericPrivate.Components+FirstLeaf, MetadataConsumer]], MetadataConsumer",
            0, isAliasFree: false);
        AssertStorageRegistration(generated,
            "GenericPrivate.Components+Stored`1[[GenericPrivate.Components+FirstLeaf, MetadataConsumer]], MetadataConsumer",
            1, isAliasFree: false);
        AssertStorageRegistration(generated,
            "GenericPrivate.Components+Stored`1[[GenericPrivate.Components+SecondLeaf, MetadataConsumer]], MetadataConsumer",
            2, isAliasFree: true);
        Assert.DoesNotContain("IndirectStorage", generated);
        Assert.DoesNotContain("RegisterJobStorage<", generated);
    }

    [Fact]
    public void PublicStorageKeepsGeneratedClassificationAcrossReferencedPrivateFields()
    {
        string generated = RunGenerator("""
namespace PublicStorage;
public struct Safe : SomeEngine.ECS.IComponent
{
    private Referenced.Outer<long>.Inner Value { get; set; }
}
public struct NativeHandle : SomeEngine.ECS.IComponent
{
    private Referenced.Outer<nuint>.Inner Value { get; set; }
}
""", ReferencedFields());

        Assert.Contains("RegisterJobStorage<global::PublicStorage.Safe>(true);", generated);
        Assert.Contains("RegisterJobStorage<global::PublicStorage.NativeHandle>(false);", generated);
        Assert.DoesNotContain("DynamicDependency", generated);
        Assert.DoesNotContain("UnsafeAccessor", generated);
    }

    private static void AssertStorageRegistration(string generated, string typeName, int index, bool isAliasFree)
    {
        Assert.Contains("if (!global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)", generated);
        string accessor = $"AliasFree{index}";
        string target = $"SomeEngine.ECS.Registry.JobStorageTypeMetadata`1[[{typeName}]], SomeEngine.ECS";
        Assert.Contains("UnsafeAccessorKind.StaticField, Name = \"s_aliasFree\"", generated);
        Assert.Contains(
            $"private static extern ref int {accessor}(\n" +
            $"        [global::System.Runtime.CompilerServices.UnsafeAccessorType(\"{target}\")] object? declaringType);",
            generated.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains(
            $"global::System.Threading.Volatile.Write(ref {accessor}(null), {(isAliasFree ? "1" : "-1")});",
            generated);
    }

    private static void AssertRoot(string generated, string typeName, string assemblyName)
    {
        string target = $"\"{typeName}\", \"{assemblyName}\")";
        string root = Assert.Single(generated.Split('\n'), line => line.Contains(target, StringComparison.Ordinal));
        Assert.DoesNotContain("+", root);
    }

    private static MetadataReference ReferencedFields()
    {
        CSharpCompilation compilation = CreateCompilation("ReferencedFields", """
namespace Referenced;
public struct Outer<T> where T : unmanaged
{
    public struct Inner
    {
        private T Value { get; set; }
    }
}
""");
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static string RunGenerator(string source, params MetadataReference[] references)
    {
        CSharpCompilation compilation = CreateCompilation("MetadataConsumer", source, references);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new EcsTypeGenerator())
            .WithUpdatedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.DoesNotContain(result.Diagnostics.Concat(output.GetDiagnostics()),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        IMethodSymbol initializer = output.GetTypeByMetadataName("SomeEngine.ECS.Generated.EcsTypeRegistration")!
            .GetMembers("RegisterTypes").OfType<IMethodSymbol>().Single();
        foreach (AttributeData attribute in initializer.GetAttributes().Where(static attribute =>
                     attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"))
        {
            string typeName = Assert.IsType<string>(attribute.ConstructorArguments[1].Value);
            string assemblyName = Assert.IsType<string>(attribute.ConstructorArguments[2].Value);
            Assert.DoesNotContain("+", typeName);
            Assert.Contains(DocumentationCommentId.GetSymbolsForDeclarationId("T:" + typeName, output),
                symbol => symbol is INamedTypeSymbol && symbol.ContainingAssembly.Identity.Name == assemblyName);
        }
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assert.Single(Assert.Single(result.Results).GeneratedSources).SourceText.ToString();
    }

    private static CSharpCompilation CreateCompilation(string name, string source, params MetadataReference[] extra)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(World).Assembly.Location));
        return CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references.Cast<MetadataReference>().Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
