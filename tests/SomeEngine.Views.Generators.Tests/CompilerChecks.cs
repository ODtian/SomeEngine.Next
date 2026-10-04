using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators.Tests;

internal static class CompilerChecks
{
    internal static async Task<ImmutableArray<Diagnostic>> Diagnostics((string Path, string Source)[] sources,
        params DiagnosticAnalyzer[] analyzers)
    {
        var (compilation, diagnostics) = Compile(sources, true, null);
        return diagnostics.AddRange(await compilation.WithAnalyzers(analyzers.ToImmutableArray()).GetAllDiagnosticsAsync());
    }

    internal static async Task<ImmutableArray<Diagnostic>> Diagnostics(string source, bool interceptors = true,
        params DiagnosticAnalyzer[] analyzers) => await Diagnostics(source, interceptors, null, analyzers);

    internal static async Task<ImmutableArray<Diagnostic>> Diagnostics(string source, bool interceptors,
        ISourceGenerator? additionalGenerator, params DiagnosticAnalyzer[] analyzers)
    {
        var (compilation, diagnostics) = Compile(source, interceptors, additionalGenerator);
        return diagnostics.AddRange(analyzers.Length == 0 ? compilation.GetDiagnostics() :
            await compilation.WithAnalyzers(analyzers.ToImmutableArray()).GetAllDiagnosticsAsync());
    }

    internal static async Task<bool> Run(string source)
    {
        var (compilation, diagnostics) = Compile(source, true, null);
        diagnostics = diagnostics.AddRange(await compilation.WithAnalyzers([new ViewAnalyzer(), new ComposeAnalyzer(),
            new SolidAnalyzer(), new FrontendAnalyzer(), new BuildWarningSuppressor()])
            .GetAllDiagnosticsAsync());
        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Error);
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Compiler.BuildAwaitChecks.Apply(image);
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        var check = (Func<bool>)assembly.GetType("ReferenceChecks")!.GetMethod("Check")!.CreateDelegate(typeof(Func<bool>));
        return check();
    }

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Compile(string source,
        bool interceptors, ISourceGenerator? additionalGenerator) => Compile([("Consumer.cs", source)], interceptors, additionalGenerator);

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Compile((string Path, string Source)[] sources,
        bool interceptors, ISourceGenerator? additionalGenerator)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp13);
        if (interceptors)
            options = options.WithFeatures([new("InterceptorsNamespaces", "SomeEngine.Views.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var input = CSharpCompilation.Create("Consumer", sources.Select(source => CSharpSyntaxTree.ParseText(source.Source, options, source.Path)),
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        ISourceGenerator[] generators = additionalGenerator is null ? [new ViewGenerator().AsSourceGenerator()] :
            [new ViewGenerator().AsSourceGenerator(), additionalGenerator];
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(input, out Compilation compilation, out var diagnostics);
        return (compilation, diagnostics);
    }
}
