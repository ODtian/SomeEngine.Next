using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class HardcodedLiteralAnalyzer
{
    public const string RuleId = "SE031";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Hardcoded literal should be named",
                "Hardcoded literal '{0}' should be moved behind a named contract or configuration value when it represents product data.",
                Rules.CategorySafety,
                DiagnosticSeverity.Warning));

    public static void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeLiteral, SyntaxKind.StringLiteralExpression, SyntaxKind.NumericLiteralExpression);
    }

    private static void AnalyzeLiteral(SyntaxNodeAnalysisContext context)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        if (IsAllowedLiteral(literal))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors[0],
            literal.GetLocation(),
            literal.Token.Text));
    }

    private static bool IsAllowedLiteral(LiteralExpressionSyntax literal)
    {
        if (literal.FirstAncestorOrSelf<AttributeSyntax>() is not null)
        {
            return true;
        }

        if (literal.FirstAncestorOrSelf<FieldDeclarationSyntax>() is FieldDeclarationSyntax diagnosticField
            && diagnosticField.Declaration.Type.ToString() == "DiagnosticDescriptor")
        {
            return true;
        }
        ObjectCreationExpressionSyntax? objectCreation = literal.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
        if (objectCreation?.Type.ToString() == "DiagnosticDescriptor")
        {
            return true;
        }

        if (objectCreation?.Type.ToString().Length == 0
            && objectCreation.Parent is EqualsValueClauseSyntax targetTypedInitializer
            && targetTypedInitializer.Parent is VariableDeclaratorSyntax targetTypedVariable
            && targetTypedVariable.Parent?.Parent is FieldDeclarationSyntax targetTypedField
            && targetTypedField.Declaration.Type.ToString() == "DiagnosticDescriptor")
        {
            return true;
        }

        if (literal.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Expression.ToString().EndsWith("Rules.Create", System.StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (literal.FirstAncestorOrSelf<EqualsValueClauseSyntax>()?.Parent is VariableDeclaratorSyntax variable
            && variable.Parent?.Parent is FieldDeclarationSyntax field
            && field.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return true;
        }

        if (literal.FirstAncestorOrSelf<EqualsValueClauseSyntax>()?.Parent is EnumMemberDeclarationSyntax)
        {
            return true;
        }

        if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            // Numeric literals in low-level code commonly encode native ABI values,
            // bit positions, alignments, GUID components, and table indices. Without
            // semantic product-domain knowledge this analyzer cannot distinguish
            // configuration data from implementation data, and forcing names for all
            // of them creates more concepts without improving readability.
            return true;
        }

        return literal.IsKind(SyntaxKind.StringLiteralExpression)
            && IsAllowedStringLiteral(literal);
    }

    private static bool IsAllowedStringLiteral(LiteralExpressionSyntax literal)
    {
        string value = literal.Token.ValueText;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (IsExceptionMessage(literal))
        {
            return true;
        }

        if (value.Length <= 1)
        {
            return true;
        }

        return !IsPersistentProductValue(literal) || !LooksLikeContractToken(value);
    }

    private static bool IsPersistentProductValue(LiteralExpressionSyntax literal)
    {
        if (literal.FirstAncestorOrSelf<ReturnStatementSyntax>() is not null)
            return true;

        if (literal.Parent is ArrowExpressionClauseSyntax arrow &&
            arrow.Parent is MethodDeclarationSyntax or PropertyDeclarationSyntax)
        {
            return true;
        }

        if (literal.FirstAncestorOrSelf<EqualsValueClauseSyntax>()?.Parent is
            VariableDeclaratorSyntax variable &&
            variable.Parent?.Parent is FieldDeclarationSyntax field)
        {
            return !field.Modifiers.Any(SyntaxKind.ConstKeyword);
        }

        return literal.FirstAncestorOrSelf<EqualsValueClauseSyntax>()?.Parent is
            PropertyDeclarationSyntax;
    }

    private static bool IsExceptionMessage(LiteralExpressionSyntax literal)
    {
        ObjectCreationExpressionSyntax? creation = literal.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
        if (creation is null)
        {
            return false;
        }

        string typeName = creation.Type.ToString();
        return typeName.EndsWith("Exception", System.StringComparison.Ordinal)
            || typeName.EndsWith("Error", System.StringComparison.Ordinal);
    }

    private static bool LooksLikeContractToken(string value)
    {
        if (value.Length <= 2)
        {
            return false;
        }

        bool hasWhitespace = value.Any(char.IsWhiteSpace);
        if (hasWhitespace)
        {
            return false;
        }

        if (value.IndexOfAny(['/', '\\', '.', ':', '_', '-']) >= 0)
        {
            return true;
        }

        bool hasUpper = value.Any(char.IsUpper);
        bool hasLower = value.Any(char.IsLower);
        return hasUpper || !hasLower;
    }
}


