using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators.Tests;

internal static class CompilerChecks
{
    internal static async Task<ImmutableArray<Diagnostic>> Diagnostics(string source, bool interceptors = true,
        params DiagnosticAnalyzer[] analyzers) => await Diagnostics(source, interceptors, null, analyzers);

    internal static async Task<ImmutableArray<Diagnostic>> Diagnostics(string source, bool interceptors,
        ISourceGenerator? additionalGenerator, params DiagnosticAnalyzer[] analyzers)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp13);
        if (interceptors)
            options = options.WithFeatures([new("InterceptorsNamespaces", "SomeEngine.Views.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var input = CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source, options, "Consumer.cs")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        ISourceGenerator[] generators = additionalGenerator is null ? [new ViewGenerator().AsSourceGenerator()] :
            [new ViewGenerator().AsSourceGenerator(), additionalGenerator];
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(input, out Compilation compilation, out var diagnostics);
        return diagnostics.AddRange(analyzers.Length == 0 ? compilation.GetDiagnostics() :
            await compilation.WithAnalyzers(analyzers.ToImmutableArray()).GetAllDiagnosticsAsync());
    }
}
