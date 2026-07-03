using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGen;
using Xunit;

namespace SomeEngine.ECS.SourceGen.Tests;

public class BundleGeneratorDiagnosticsTests
{
    [Fact]
    public void ReportsDuplicateComponentFields()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct Position : SomeEngine.ECS.Components.IComponent
{
    public int X;
}

public struct DuplicateBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position First;
    public Position Second;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG003");
    }

    [Fact]
    public void ReportsRelationFields()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct Likes : SomeEngine.ECS.Components.IRelation
{
    public int Value;
}

public struct RelationBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Likes Relation;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG002");
    }

    [Fact]
    public void ReportsNonComponentFields()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct InvalidBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public int Count;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG001");
    }

    [Fact]
    public void ReportsRecursiveBundles()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct RecursiveBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public RecursiveBundle Self;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG004");
    }

    [Fact]
    public void ReportsDirectBufferElementFields()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct Item : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct InvalidBufferBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Item Item;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG006");
    }

    [Fact]
    public void ReportsDirectSharedComponentFields()
    {
        var diagnostics = RunGenerator("""
using SomeEngine.ECS.Components;

public struct Scene : SomeEngine.ECS.Components.ISharedComponent
{
    public int Value;
}

public struct InvalidSharedBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Scene Scene;
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SECSSG007");
    }

    private static Diagnostic[] RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorDiagnostics",
            syntaxTrees: [syntaxTree],
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new BundleGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        var runResult = driver.GetRunResult();

        return generatorDiagnostics
            .Concat(runResult.Results.SelectMany(result => result.Diagnostics))
            .Concat(outputCompilation.GetDiagnostics())
            .ToArray();
    }

    private static MetadataReference[] GetReferences()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(SomeEngine.ECS.World).Assembly.Location));
        return references.ToArray();
    }
}
