using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// C# lint: 'var' usage. Reads allowed contexts from harness/config.json
/// (Style.allowedVarContexts). var is OK only when the RHS makes the type
/// obvious; otherwise the agent must use an explicit type for legibility.
/// </summary>
internal static class VarUsageAnalyzer
{
    public const string RuleId = "SE010";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Avoid var for non-obvious types",
                "Use explicit type instead of 'var' when the type is not obvious from the RHS. Agent legibility requires visible types. See harness/config.json#style.",
                Rules.CategoryStyle,
                DiagnosticSeverity.Warning));

    public static void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(ctx =>
        {
            var config = AnalyzerConfigLoader.Load(ctx.Options);
            var allowed = new HashSet<string>(config.Style.AllowedVarContexts);

            ctx.RegisterSyntaxNodeAction(
                c => Analyze(c, allowed),
                SyntaxKind.VariableDeclaration);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx, HashSet<string> allowed)
    {
        var decl = (VariableDeclarationSyntax)ctx.Node;
        if (!decl.Type.IsVar) return;

        foreach (var v in decl.Variables)
        {
            if (v.Initializer is null) continue;
            var rhs = v.Initializer.Value;

            if (!IsObviousTypeContext(rhs, allowed))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors[0], decl.Type.GetLocation()));
                return;
            }
        }
    }

    private static bool IsObviousTypeContext(ExpressionSyntax rhs, HashSet<string> allowed)
    {
        return rhs switch
        {
            ObjectCreationExpressionSyntax => allowed.Contains("ObjectCreation"),
            ImplicitObjectCreationExpressionSyntax => allowed.Contains("ImplicitObjectCreation"),
            ArrayCreationExpressionSyntax => allowed.Contains("ArrayCreation"),
            ImplicitArrayCreationExpressionSyntax => allowed.Contains("ImplicitArrayCreation"),
            StackAllocArrayCreationExpressionSyntax => allowed.Contains("StackAllocArrayCreation"),
            CastExpressionSyntax => allowed.Contains("Cast"),
            DefaultExpressionSyntax => allowed.Contains("DefaultExpression"),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression) =>
                allowed.Contains("DefaultExpression"),
            ElementAccessExpressionSyntax => allowed.Contains("ElementAccess"),
            InvocationExpressionSyntax => allowed.Contains("Invocation"),
            TupleExpressionSyntax => allowed.Contains("Tuple"),
            _ => false,
        };
    }
}
