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

    private static readonly DiagnosticDescriptor DirectCallRequired = new(
        "SEV008", "Call composable methods directly", "Call composable method '{0}' directly in source so the compiler can supply its call-site key",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor CapturedLambdaAllocation = new(
        "SEV013", "Pass state to a static lambda", "调用方每次重跑都会分配；使用 static lambda 并显式传入状态参数",
        "Views", DiagnosticSeverity.Info, true);

    private static readonly DiagnosticDescriptor ContentDelegateRequired = new(
        "SEV014", "Declare a view content delegate", "Content parameter '{0}' requires a delegate returning void or BuildTask with value parameters",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor RememberMethodRequired = new(
        "SEV009", "Remember values in composable methods", "Mark the method calling Remember with Composable",
        "Views", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [StaticMethodRequired, DirectCallRequired, RememberMethodRequired,
            CapturedLambdaAllocation, ContentDelegateRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(CheckLambdaAllocation,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
        context.RegisterSymbolAction(static analysis =>
        {
            var parameter = (IParameterSymbol)analysis.Symbol;
            if (parameter.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute") &&
                !ViewCalls.IsContentDelegate(parameter.Type))
                analysis.ReportDiagnostic(Diagnostic.Create(ContentDelegateRequired, parameter.Locations[0], parameter.Name));
        }, SymbolKind.Parameter);
        context.RegisterSymbolAction(static analysis =>
        {
            var method = (IMethodSymbol)analysis.Symbol;
            if (!ViewCalls.IsComposable(method))
                return;
            if (!method.IsStatic)
                analysis.ReportDiagnostic(Diagnostic.Create(StaticMethodRequired, method.Locations[0], method.Name));
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

    private static void CheckLambdaAllocation(SyntaxNodeAnalysisContext analysis)
    {
        if (analysis.IsGeneratedCode || analysis.Node is not AnonymousFunctionExpressionSyntax lambda)
            return;
        SyntaxNode expression = lambda;
        while (expression.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            expression = expression.Parent;
        if (expression.Parent is not ArgumentSyntax ||
            lambda.Ancestors().TakeWhile(node => node is not MethodDeclarationSyntax)
                .Any(node => node is AnonymousFunctionExpressionSyntax))
            return;
        MethodDeclarationSyntax? declaration = lambda.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (declaration is null || analysis.SemanticModel.GetDeclaredSymbol(declaration, analysis.CancellationToken) is not IMethodSymbol method ||
            (!ViewCalls.IsComposable(method) && !IsRerunBuild(analysis, method)))
            return;
        DataFlowAnalysis? captures = analysis.SemanticModel.AnalyzeDataFlow(lambda);
        if (captures is { Succeeded: true } && captures.CapturedInside.Any(symbol =>
                symbol.Locations.All(location => location.SourceTree != lambda.SyntaxTree || !lambda.Span.Contains(location.SourceSpan))))
            analysis.ReportDiagnostic(Diagnostic.Create(CapturedLambdaAllocation, lambda.GetLocation()));
    }

    private static bool IsRerunBuild(SyntaxNodeAnalysisContext analysis, IMethodSymbol method)
    {
        ISymbol? build = analysis.Compilation.GetTypeByMetadataName("SomeEngine.Views.IView")?.GetMembers("Build").FirstOrDefault();
        if (build is null || !SymbolEqualityComparer.Default.Equals(method, method.ContainingType.FindImplementationForInterfaceMember(build)))
            return false;
        IPropertySymbol? mode = method.ContainingType.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(property =>
            property.IsStatic && (property.Name == "Mode" || property.ExplicitInterfaceImplementations.Any(value =>
                value.Name == "Mode" && value.ContainingType.ToDisplayString() == "SomeEngine.Views.IView")));
        if (mode?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(analysis.CancellationToken) is
            PropertyDeclarationSyntax { ExpressionBody.Expression: { } value })
        {
            if (value.SyntaxTree == analysis.SemanticModel.SyntaxTree)
            {
                Optional<object?> constant = analysis.SemanticModel.GetConstantValue(value, analysis.CancellationToken);
                if (constant.HasValue && Convert.ToInt32(constant.Value, System.Globalization.CultureInfo.InvariantCulture) == 1)
                    return false;
            }
        }
        return true;
    }
}
