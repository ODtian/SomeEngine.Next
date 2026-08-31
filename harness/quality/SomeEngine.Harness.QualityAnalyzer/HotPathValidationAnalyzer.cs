using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class HotPathValidationAnalyzer
{
    public const string RuleId = "SE032";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(
                RuleId,
                "Hot-path validation is Forbidden",
                "Validation '{0}' must use the unified development check or remain at an external input boundary.",
                Rules.CategorySafety));

    public static void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (!IsPerformanceSensitiveLoop(invocation))
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
            !IsScatteredValidation(method))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors[0],
            invocation.GetLocation(),
            method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (!IsPerformanceSensitiveLoop(creation))
            return;

        if (context.SemanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol type ||
            !IsHotValidationException(type))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors[0],
            creation.GetLocation(),
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
    }

    private static bool IsScatteredValidation(IMethodSymbol method)
    {
        if (method.Name.StartsWith("Validate", StringComparison.Ordinal))
            return !HasConditionalAttribute(method);

        string typeName = method.ContainingType?.ToDisplayString() ?? string.Empty;
        return method.Name.StartsWith("ThrowIf", StringComparison.Ordinal) &&
            typeName is "System.ArgumentOutOfRangeException" or "System.ObjectDisposedException";
    }

    private static bool HasConditionalAttribute(IMethodSymbol method) =>
        method.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute");

    private static bool IsHotValidationException(INamedTypeSymbol type)
    {
        string name = type.ToDisplayString();
        return name is "System.ArgumentOutOfRangeException" or "System.ObjectDisposedException";
    }

    private static bool IsPerformanceSensitiveLoop(SyntaxNode node)
    {
        BaseNamespaceDeclarationSyntax? declaration = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null || !IsPerformanceSensitiveNamespace(declaration.Name.ToString()))
            return false;

        return node.Ancestors().Any(static ancestor => ancestor is
            ForStatementSyntax or
            ForEachStatementSyntax or
            ForEachVariableStatementSyntax or
            WhileStatementSyntax or
            DoStatementSyntax);
    }

    private static bool IsPerformanceSensitiveNamespace(string value) =>
        value == "SomeEngine.Graphics" ||
        IsNamespaceOrChild(value, "SomeEngine.Render") ||
        IsNamespaceOrChild(value, "SomeEngine.RenderGraph");

    private static bool IsNamespaceOrChild(string value, string root) =>
        value == root || value.StartsWith(root + ".", StringComparison.Ordinal);
}
