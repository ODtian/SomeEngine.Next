using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ViewAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor NamespaceRequired = new(
        "SEV001", "Enable view interceptors", "Add SomeEngine.Views.Generated to InterceptorsNamespaces",
        "Views", DiagnosticSeverity.Error, true, customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    private static readonly DiagnosticDescriptor DirectCallRequired = new(
        "SEV004", "Use intercepted view calls", "Call '{0}' directly in source so the compiler can provide its node identity; use View(in T, ulong) for an explicit identity",
        "Views", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [NamespaceRequired, DirectCallRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(static analysis =>
        {
            if (analysis.Compilation.GetTypeByMetadataName("SomeEngine.Views.IView") is null)
                return;
            bool enabled = analysis.Compilation.SyntaxTrees.Any(tree =>
                tree.Options is CSharpParseOptions options &&
                options.Features.TryGetValue("InterceptorsNamespaces", out string namespaces) &&
                namespaces.Split(';').Any(value => value.Length > 0 &&
                    (value == "SomeEngine.Views.Generated" || "SomeEngine.Views.Generated".StartsWith(value + ".", StringComparison.Ordinal))));
            if (!enabled)
                analysis.ReportDiagnostic(Diagnostic.Create(NamespaceRequired, Location.None));
        });
        context.RegisterSyntaxNodeAction(static analysis =>
        {
            var name = (SimpleNameSyntax)analysis.Node;
            if (analysis.SemanticModel.GetSymbolInfo(name, analysis.CancellationToken).Symbol is not IMethodSymbol method ||
                (!ViewCalls.IsView(method) && !ViewCalls.IsViewFunction(method)))
                return;
            ExpressionSyntax expression = name.Parent is MemberAccessExpressionSyntax member ? member : name;
            if (expression.Parent is InvocationExpressionSyntax call && call.Expression == expression)
            {
                if (analysis.SemanticModel.GetInterceptorMethod(call, analysis.CancellationToken) is not null)
                    return;
            }
            else if (expression.Ancestors().OfType<InvocationExpressionSyntax>()
                .Any(value => value.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
                return;
            analysis.ReportDiagnostic(Diagnostic.Create(DirectCallRequired, name.GetLocation(), method.Name));
        }, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }
}
