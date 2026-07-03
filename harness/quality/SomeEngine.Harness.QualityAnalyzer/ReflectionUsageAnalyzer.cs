using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class ReflectionUsageAnalyzer
{
    public const string RuleId = "SE030";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Reflection usage is Forbidden",
                "Reflection API '{0}' is forbidden in migrated production code.",
                Rules.CategorySafety));

    public static void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol;
        if (symbol is null || !IsReflectionSymbol(symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors[0],
            invocation.GetLocation(),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(creation).Symbol;
        if (symbol is null || !IsReflectionSymbol(symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors[0],
            creation.GetLocation(),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
    }

    private static bool IsReflectionSymbol(ISymbol symbol)
    {
        var containingType = symbol.ContainingType;
        if (containingType is not null && IsReflectionType(containingType))
        {
            return true;
        }

        return IsReflectionNamespace(symbol.ContainingNamespace);
    }

    private static bool IsReflectionType(ITypeSymbol type)
        => type.ToDisplayString() == "System.Type" || IsReflectionNamespace(type.ContainingNamespace);

    private static bool IsReflectionNamespace(INamespaceSymbol? ns)
        => ns is not null && ns.ToDisplayString().StartsWith("System.Reflection");
}



