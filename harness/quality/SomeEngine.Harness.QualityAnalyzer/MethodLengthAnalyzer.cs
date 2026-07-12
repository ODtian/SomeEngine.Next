using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Method length gate. Methods exceeding the line threshold must be split.
/// Flame-long methods are the primary vehicle for AI patch-stacking.
/// </summary>
internal static class MethodLengthAnalyzer
{
    public const string RuleId = "SE021";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Method too long",
                "Method '{0}' is {1} lines (max {2}). Extract responsibilities into smaller methods. See harness/config.json#complexity.",
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
        if (method.Body is null && method.ExpressionBody is null) return;
        int max = config.Complexity.MaxMethodLines;

        var span = method.Body ?? (SyntaxNode)method.ExpressionBody!;
        int lines = span.SyntaxTree.GetLineSpan(span.Span).EndLinePosition.Line
                  - span.SyntaxTree.GetLineSpan(span.Span).StartLinePosition.Line
                  + 1;

        if (lines > max &&
            !AcceptedDiagnosticBaseline.Contains(ctx, config, RuleId, method.Identifier, lines))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors[0],
                method.Identifier.GetLocation(),
                method.Identifier.ValueText,
                lines,
                max));
        }
    }
}
