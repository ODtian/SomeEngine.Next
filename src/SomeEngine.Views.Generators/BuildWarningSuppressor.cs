using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BuildWarningSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor SynchronousBuild = new(
        "SEVS001", "CS1998", "IView.Build uses BuildTask for both synchronous and asynchronous content.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [SynchronousBuild];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        INamedTypeSymbol? view = context.Compilation.GetTypeByMetadataName("SomeEngine.Views.IView");
        ISymbol? build = view?.GetMembers("Build").FirstOrDefault();
        if (build is null)
            return;
        foreach (Diagnostic diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Id != "CS1998" || diagnostic.Location.SourceTree is not SyntaxTree tree)
                continue;
            var declaration = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan)
                .FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (declaration is null)
                continue;
            SemanticModel model = context.GetSemanticModel(tree);
            var method = model.GetDeclaredSymbol(declaration, context.CancellationToken) as IMethodSymbol;
            if (method is not null && SymbolEqualityComparer.Default.Equals(method,
                method.ContainingType.FindImplementationForInterfaceMember(build)))
                context.ReportSuppression(Suppression.Create(SynchronousBuild, diagnostic));
        }
    }
}
