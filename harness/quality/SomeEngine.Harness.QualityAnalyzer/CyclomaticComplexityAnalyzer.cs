using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Cyclomatic complexity gate. Methods exceeding the threshold must be split.
/// Threshold is a structural limit, not a tunable parameter — if a method
/// genuinely needs more branches, split it into smaller methods.
/// </summary>
internal static class CyclomaticComplexityAnalyzer
{
    public const string RuleId = "SE020";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Cyclomatic complexity too high",
                "Method '{0}' has cyclomatic complexity {1} (max {2}). Split into smaller methods. See harness/config.json#complexity.",
                Rules.CategoryComplexity));

    public static void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(ctx =>
        {
            var config = AnalyzerConfigLoader.Load(ctx.Options);
            ctx.RegisterSyntaxNodeAction(
                c => Analyze(c, config),
                SyntaxKind.MethodDeclaration);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx, AnalyzerHarnessConfig config)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        int max = config.Complexity.MaxCyclomaticComplexity;
        int complexity = 1 + method.DescendantNodes()
            .Count(IsBranchNode);

        if (complexity > max &&
            !AcceptedDiagnosticBaseline.Contains(ctx, config, RuleId, method.Identifier, complexity))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors[0],
                method.Identifier.GetLocation(),
                method.Identifier.ValueText,
                complexity,
                max));
        }
    }

    private static bool IsBranchNode(SyntaxNode n)
    {
        if (n is IfStatementSyntax
            or ConditionalExpressionSyntax
            or SwitchStatementSyntax
            or WhileStatementSyntax
            or ForStatementSyntax
            or ForEachStatementSyntax
            or DoStatementSyntax
            or CatchClauseSyntax)
        {
            return true;
        }

        if (n is BinaryExpressionSyntax b
            && (b.IsKind(SyntaxKind.LogicalAndExpression)
                || b.IsKind(SyntaxKind.LogicalOrExpression)))
        {
            return true;
        }

        return false;
    }
}
