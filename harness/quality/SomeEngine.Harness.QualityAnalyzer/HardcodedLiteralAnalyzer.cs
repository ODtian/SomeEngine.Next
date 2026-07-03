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
            return IsAllowedNumericLiteral(literal.Token.Text);
        }

        return literal.IsKind(SyntaxKind.StringLiteralExpression)
            && IsAllowedStringLiteral(literal);
    }

    private static bool IsAllowedNumericLiteral(string text)
    {
        string normalized = text
            .TrimEnd('u', 'U', 'l', 'L', 'f', 'F', 'd', 'D', 'm', 'M');

        if (normalized is "0" or "1" or "0.0" or "1.0" or "0.5" or "2.0")
        {
            return true;
        }

        if (int.TryParse(normalized, out int integer))
        {
            return integer >= 0 && integer <= 16;
        }

        return false;
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

        return !LooksLikeContractToken(value);
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
        if (value.IndexOfAny(['/', '\\', '.', ':', '_', '-']) >= 0)
        {
            return true;
        }

        if (value.Length <= 2)
        {
            return false;
        }

        bool hasWhitespace = value.Any(char.IsWhiteSpace);
        if (hasWhitespace)
        {
            return false;
        }

        bool hasUpper = value.Any(char.IsUpper);
        bool hasLower = value.Any(char.IsLower);
        return hasUpper || !hasLower;
    }
}


