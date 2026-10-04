using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SolidAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor StaticMethodRequired = new(
        "SEV016", "Declare a static Solid component", "Declare Solid component '{0}' as static",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor ContentMethodRequired = new(
        "SEV017", "Declare Solid view content", "Solid component '{0}' requires void or BuildTask content with value parameters",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor DirectCallRequired = new(
        "SEV018", "Declare Solid views through direct calls", "Call Solid component '{0}' directly in source from Compose or Build so the compiler can declare its Once view",
        "Views", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [StaticMethodRequired, ContentMethodRequired, DirectCallRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(static analysis =>
        {
            var method = (IMethodSymbol)analysis.Symbol;
            if (!ViewCalls.IsComponent(method))
                return;
            if (!method.IsStatic)
                analysis.ReportDiagnostic(Diagnostic.Create(StaticMethodRequired, method.Locations[0], method.Name));
            else if (!ViewCalls.IsSolidComponent(method))
                analysis.ReportDiagnostic(Diagnostic.Create(ContentMethodRequired, method.Locations[0], method.Name));
        }, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(static analysis =>
        {
            var name = (SimpleNameSyntax)analysis.Node;
            if (analysis.SemanticModel.GetSymbolInfo(name, analysis.CancellationToken).Symbol is not IMethodSymbol method ||
                !ViewCalls.IsSolidComponent(method) || !ViewCalls.IsComponentCall(analysis.SemanticModel, name.SpanStart))
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
