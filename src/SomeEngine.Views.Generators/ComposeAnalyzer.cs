using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComposeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor StaticMethodRequired = new(
        "SEV006", "Declare a static composable method", "Declare composable method '{0}' as static",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BuildResultRequired = new(
        "SEV007", "Return void or BuildTask from a composable view", "Return void or BuildTask from composable method '{0}'; value-producing groups use Inline",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor DirectCallRequired = new(
        "SEV008", "Call composable methods directly", "Call composable method '{0}' directly in source so the compiler can declare its view",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor RememberMethodRequired = new(
        "SEV009", "Remember values in composable methods", "Mark the method calling Remember with Composable",
        "Views", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [StaticMethodRequired, BuildResultRequired, DirectCallRequired, RememberMethodRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(static analysis =>
        {
            var method = (IMethodSymbol)analysis.Symbol;
            if (!ViewCalls.IsComposable(method))
                return;
            if (!method.IsStatic)
                analysis.ReportDiagnostic(Diagnostic.Create(StaticMethodRequired, method.Locations[0], method.Name));
            if (!method.ReturnsVoid && method.ReturnType.ToDisplayString() != "SomeEngine.Views.BuildTask" &&
                !method.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.InlineAttribute"))
                analysis.ReportDiagnostic(Diagnostic.Create(BuildResultRequired, method.Locations[0], method.Name));
        }, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(static analysis =>
        {
            var name = (SimpleNameSyntax)analysis.Node;
            if (analysis.SemanticModel.GetSymbolInfo(name, analysis.CancellationToken).Symbol is not IMethodSymbol method)
                return;
            bool remember = ViewCalls.IsRemember(method);
            if (!remember && !ViewCalls.IsComposable(method))
                return;
            ExpressionSyntax expression = name.Parent is MemberAccessExpressionSyntax member ? member : name;
            if (expression.Parent is InvocationExpressionSyntax call && call.Expression == expression)
            {
                if (remember && (analysis.SemanticModel.GetEnclosingSymbol(call.SpanStart, analysis.CancellationToken) is not IMethodSymbol caller ||
                    !ViewCalls.IsComposable(caller)))
                    analysis.ReportDiagnostic(Diagnostic.Create(RememberMethodRequired, name.GetLocation()));
                if (analysis.SemanticModel.GetEnclosingSymbol(call.SpanStart, analysis.CancellationToken) is IMethodSymbol enclosing &&
                    ViewCalls.IsComposeBuild(enclosing))
                    return;
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
