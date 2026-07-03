using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Class size gate. Rejects classes with too many methods or fields.
/// Thresholds from harness/config.json#complexity (MaxMethodsPerClass,
/// MaxFieldsPerClass). A class doing too much violates SRP.
/// </summary>
internal static class ClassSizeAnalyzer
{
    public const string RuleIdMethods = "SE022";
    public const string RuleIdFields = "SE023";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleIdMethods,
                "Class has too many methods",
                "Class '{0}' has {1} methods (max {2}). Split responsibilities. See harness/config.json#complexity.",
                Rules.CategoryComplexity),
            Rules.Create(RuleIdFields,
                "Class has too many fields",
                "Class '{0}' has {1} fields (max {2}). Split responsibilities. See harness/config.json#complexity.",
                Rules.CategoryComplexity));

    public static void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(ctx =>
        {
            var config = AnalyzerConfigLoader.Load(ctx.Options);
            int maxMethods = config.Complexity.MaxMethodsPerClass;
            int maxFields = config.Complexity.MaxFieldsPerClass;

            ctx.RegisterSyntaxNodeAction(
                c => Analyze(c, maxMethods, maxFields),
                SyntaxKind.ClassDeclaration);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx, int maxMethods, int maxFields)
    {
        var cls = (ClassDeclarationSyntax)ctx.Node;
        var name = cls.Identifier.ValueText;

        int methods = cls.Members.Count(m => m is MethodDeclarationSyntax);
        if (methods > maxMethods)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors[0], cls.Identifier.GetLocation(), name, methods, maxMethods));
        }

        int fields = cls.Members
            .Where(m => m is FieldDeclarationSyntax)
            .Cast<FieldDeclarationSyntax>()
            .Sum(f => f.Declaration.Variables.Count);
        if (fields > maxFields)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors[1], cls.Identifier.GetLocation(), name, fields, maxFields));
        }
    }
}
