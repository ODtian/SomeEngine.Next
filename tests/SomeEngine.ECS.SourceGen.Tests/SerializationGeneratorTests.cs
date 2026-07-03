using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGen;
using Xunit;

namespace SomeEngine.ECS.SourceGen.Tests;

public class SerializationGeneratorTests
{
    [Fact]
    public void EmitsRegisterAllCodecsAndPatchers()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

[SerializableComponent("11111111-1111-1111-1111-111111111111")]
public partial struct GeneratedNested : SomeEngine.ECS.Components.IComponent
{
    public Entity Target;
}

[SerializableComponent("22222222-2222-2222-2222-222222222222")]
public partial struct GeneratedPosition : SomeEngine.ECS.Components.IComponent
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
public partial struct FirstGenerated : SomeEngine.ECS.Components.IComponent
{
    public int X;
}

[SerializableComponent("33333333-3333-3333-3333-333333333333")]
public partial struct SecondGenerated : SomeEngine.ECS.Components.IComponent
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
public partial struct InvalidGenerated : SomeEngine.ECS.Components.IComponent
{
    public object Value;
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSER003");
    }

    private static GeneratorRunResult RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            assemblyName: "SerializationGeneratorTests",
            syntaxTrees: [syntaxTree],
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
