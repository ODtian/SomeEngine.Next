using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SomeEngine.Generators;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetUseBehaviorTests
{
    [Theory]
    [InlineData("public Asset Value { get; set; }")]
    [InlineData("private AssetFile value;")]
    [InlineData("public System.Collections.Generic.List<AssetId<Asset>> Values { get; set; }")]
    [InlineData("public static Holder Value { get; set; }")]
    [InlineData("public Asset ReturnAsset() => null;")]
    [InlineData("public void Set(Asset value) { }")]
    public async Task ExplicitUseTypesRejectDirectAndNestedAssetAccess(string member)
    {
        string source = "using SomeEngine.Assets; using SomeEngine.Serialization; public class Holder { public Asset Value; } "
            + "[AssetUse] public class Use { " + member + " }";
        ImmutableArray<Diagnostic> diagnostics = await Analyze(source);
        Diagnostic error = Assert.Single(diagnostics.Where(static error => error.Id == "SEAU001"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.True(error.Location.IsInSource);
        Assert.Contains("Use", error.GetMessage());
    }

    [Fact]
    public async Task UseInterfaceAppliesToItsImplementationWithoutMethodNameHeuristics()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            using SomeEngine.Assets;
            using SomeEngine.Serialization;
            [AssetUse] public interface ICalculation { int Evaluate(); }
            public class Calculation : ICalculation
            {
                private Asset file;
                public int Evaluate() => 1;
            }
            public class Tool { public Asset Read() => null; }
            """);
        Diagnostic error = Assert.Single(diagnostics.Where(static error => error.Id == "SEAU001"));
        Assert.Contains("Calculation", error.GetMessage());
    }

    [Fact]
    public async Task AssetToolsAndIndependentUseValuesAreAllowed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            using SomeEngine.Assets;
            using SomeEngine.Serialization;
            using SomeEngine.Serialization.Containers;
            public class Tool { public Asset Value; public AssetId<Asset> Id; }
            public readonly record struct Point(float X, float Y);
            [AssetUse] public class Use
            {
                private System.ReadOnlyMemory<byte> bytes;
                public Point[] Points { get; set; }
                public int Read() => Points.Length;
            }
            """);
        Assert.DoesNotContain(diagnostics, static error => error.Severity == DiagnosticSeverity.Error);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths.Add(typeof(Asset).Assembly.Location);
        paths.Add(typeof(AssetUseAttribute).Assembly.Location);
        var compilation = CSharpCompilation.Create("AssetUseConsumer", [CSharpSyntaxTree.ParseText(source)],
            paths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), static error => error.Severity == DiagnosticSeverity.Error);
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AssetUseAnalyzer())).GetAnalyzerDiagnosticsAsync();
    }
}
