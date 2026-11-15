using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS.SourceGen;
using Xunit;

namespace SomeEngine.ECS.SourceGen.Tests;

public class SerializationGeneratorTests
{
    [Fact]
    public void ReportsDuplicateStableIds()
    {
        var result = RunGenerator("""
namespace SomeEngine.ECS.SourceGen.TestInput;

using SomeEngine.ECS.Components;
using SomeEngine.ECS.Serialization;

[SerializableComponent("68ea901f-a7e6-4631-bb8e-87db43a62d2e")]
public partial struct FirstGenerated : SomeEngine.ECS.IComponent
{
    public int X;
}

[SerializableComponent("68ea901f-a7e6-4631-bb8e-87db43a62d2e")]
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

[SerializableComponent("e6bb16d7-4f90-4627-a74b-cd89b2270bba")]
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
