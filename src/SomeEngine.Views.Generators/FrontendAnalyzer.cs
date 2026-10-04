using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class FrontendAnalyzer : DiagnosticAnalyzer
{
    private enum Frontend { None, Compose, SwiftUI, Solid }

    private static readonly DiagnosticDescriptor MixedPrimitives = new(
        "SEV019", "Use one frontend per function body",
        "Use {0} primitives in this function body; '{1}' belongs to {2}",
        "Views", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor ReactiveValueArgument = new(
        "SEV020", "Keep cross-frontend values reactive",
        "This Solid component evaluates '{0}' once; use Insert(() => ...) around the component call or pass State<T> directly",
        "Views", DiagnosticSeverity.Info, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MixedPrimitives, ReactiveValueArgument];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(CheckFunction, SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
    }

    private static void CheckFunction(SyntaxNodeAnalysisContext analysis)
    {
        var (method, body) = GetFunction(analysis);
        if (method is null || body is null)
            return;
        SyntaxNode[] nodes = body.DescendantNodesAndSelf(node =>
            node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).ToArray();
        var primitives = new List<(SyntaxNode Syntax, Frontend Frontend, string Name)>();
        foreach (SyntaxNode node in nodes)
        {
            if (node is InvocationExpressionSyntax call &&
                analysis.SemanticModel.GetSymbolInfo(call, analysis.CancellationToken).Symbol is IMethodSymbol primitive)
            {
                Frontend frontend = GetPrimitiveFrontend(primitive);
                if (frontend != Frontend.None)
                    primitives.Add((call, frontend, primitive.Name));
            }
            else if (node is SimpleNameSyntax name && !name.Ancestors().OfType<InvocationExpressionSyntax>()
                .Any(call => analysis.SemanticModel.GetOperation(call, analysis.CancellationToken) is INameOfOperation))
            {
                ISymbol? symbol = analysis.SemanticModel.GetSymbolInfo(name, analysis.CancellationToken).Symbol;
                if (symbol is IPropertySymbol property && StatePropertyDeclarations.HasStateAttribute(property))
                    primitives.Add((name, Frontend.SwiftUI, property.Name));
                else if (symbol is IMethodSymbol reference)
                {
                    SyntaxNode expression = name.Parent is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax
                        ? name.Parent : name;
                    if (expression.Parent is InvocationExpressionSyntax directCall && directCall.Expression == expression)
                        continue;
                    Frontend frontend = GetPrimitiveFrontend(reference);
                    if (frontend != Frontend.None)
                        primitives.Add((name, frontend, reference.Name));
                }
            }
        }

        Frontend bodyFrontend = GetDeclaredFrontend(method, analysis.Compilation);
        if (bodyFrontend == Frontend.None && primitives.Any(value => value.Frontend == Frontend.Solid))
            bodyFrontend = Frontend.Solid;
        if (bodyFrontend == Frontend.None)
            return;
        foreach (var primitive in primitives)
            if (primitive.Frontend != bodyFrontend)
                analysis.ReportDiagnostic(Diagnostic.Create(MixedPrimitives, primitive.Syntax.GetLocation(),
                    bodyFrontend, primitive.Name, primitive.Frontend));
        if (bodyFrontend == Frontend.Solid)
        {
            LocalValues? locals = null;
            foreach (var call in nodes.OfType<InvocationExpressionSyntax>())
            {
                if (analysis.SemanticModel.GetOperation(call, analysis.CancellationToken) is not IInvocationOperation operation)
                    continue;
                if (ViewCalls.IsComposeView(operation.TargetMethod))
                    CheckArguments(analysis, operation.Arguments,
                        locals ??= new LocalValues(analysis.Node, nodes, analysis.SemanticModel, analysis.CancellationToken));
                else if (GetViewType(operation.TargetMethod) is { } view && IsRerunView(view, analysis.SemanticModel, analysis.CancellationToken))
                {
                    locals ??= new LocalValues(analysis.Node, nodes, analysis.SemanticModel, analysis.CancellationToken);
                    if (ViewCalls.IsViewFunction(operation.TargetMethod))
                        CheckArguments(analysis, operation.Arguments, locals);
                    else if (operation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value is { } value &&
                        GetViewValue(value, locals) is { } creation)
                        CheckViewValues(analysis, creation, locals);
                }
            }
        }
    }

    private static INamedTypeSymbol? GetViewType(IMethodSymbol method)
    {
        if (method.Name == "View" && method.ContainingType.ToDisplayString() == "SomeEngine.Views.Scope")
            return method.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        if (!ViewCalls.IsViewFunction(method))
            return null;
        return (method.ContainingType.ContainingType?.GetTypeMembers(method.Name, method.TypeArguments.Length) ??
            method.ContainingNamespace.GetTypeMembers(method.Name, method.TypeArguments.Length)).FirstOrDefault();
    }

    private static bool IsRerunView(INamedTypeSymbol view, SemanticModel model, CancellationToken cancellation)
    {
        if (!view.AllInterfaces.Any(type => type.ToDisplayString() == "SomeEngine.Views.IView"))
            return false;
        if (view.GetMembers().OfType<IPropertySymbol>().Any(StatePropertyDeclarations.HasStateAttribute))
            return true;
        IPropertySymbol? mode = view.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(property => property.IsStatic &&
            (property.Name == "Mode" || property.ExplicitInterfaceImplementations.Any(value =>
                value.Name == "Mode" && value.ContainingType.ToDisplayString() == "SomeEngine.Views.IView")));
        if (mode is null)
            return true;
        if (mode.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is not PropertyDeclarationSyntax declaration)
            return false;
        ExpressionSyntax? expression = declaration.ExpressionBody?.Expression ?? declaration.AccessorList?.Accessors
            .FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))?.ExpressionBody?.Expression;
        if (expression is null)
            return false;
        // Explicit view modes can be declared in a different file from the component call.
#pragma warning disable RS1030
        SemanticModel declarationModel = expression.SyntaxTree == model.SyntaxTree ? model :
            model.Compilation.GetSemanticModel(expression.SyntaxTree);
#pragma warning restore RS1030
        Optional<object?> constant = declarationModel.GetConstantValue(expression, cancellation);
        return constant.HasValue && Convert.ToInt32(constant.Value, System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    private static IObjectCreationOperation? GetViewValue(IOperation value, LocalValues locals) => value switch
    {
        IConversionOperation conversion => GetViewValue(conversion.Operand, locals),
        ILocalReferenceOperation local => locals.GetValue(local) is { } localValue ? GetViewValue(localValue, locals) : null,
        IObjectCreationOperation creation => creation,
        _ => null,
    };

    private static void CheckArguments(SyntaxNodeAnalysisContext analysis, ImmutableArray<IArgumentOperation> arguments, LocalValues locals)
    {
        foreach (IArgumentOperation argument in arguments)
            if (argument.Syntax is ArgumentSyntax syntax && argument.Parameter?.Type.TypeKind != TypeKind.Delegate &&
                locals.HasReactiveRead(argument.Value))
                analysis.ReportDiagnostic(Diagnostic.Create(ReactiveValueArgument, syntax.Expression.GetLocation(), argument.Parameter?.Name));
    }

    private static void CheckViewValues(SyntaxNodeAnalysisContext analysis, IObjectCreationOperation creation, LocalValues locals)
    {
        CheckArguments(analysis, creation.Arguments, locals);
        if (creation.Initializer is not { } initializer)
            return;
        foreach (ISimpleAssignmentOperation assignment in initializer.Initializers.OfType<ISimpleAssignmentOperation>())
            if (locals.HasReactiveRead(assignment.Value))
                analysis.ReportDiagnostic(Diagnostic.Create(ReactiveValueArgument, assignment.Value.Syntax.GetLocation(),
                    assignment.Target switch
                    {
                        IPropertyReferenceOperation property => property.Property.Name,
                        IFieldReferenceOperation field => field.Field.Name,
                        _ => "value",
                    }));
    }

    private static bool IsSharedReference(ITypeSymbol? type) => type?.TypeKind == TypeKind.Delegate || type is INamedTypeSymbol named &&
        (named.ContainingNamespace.ToDisplayString() == "SomeEngine.Views" && named.MetadataName is "State`1" or "Memo`1" ||
            named.ContainingNamespace.ToDisplayString() == "SomeEngine.Views.SwiftUI" && named.MetadataName == "Binding`1");

    private static bool IsReactiveProperty(IPropertySymbol property) => property.Name == "Value" &&
        property.ContainingType.ContainingNamespace.ToDisplayString() == "SomeEngine.Views" &&
        property.ContainingType.MetadataName is "State`1" or "Memo`1";

    private static (IMethodSymbol? Method, SyntaxNode? Body) GetFunction(SyntaxNodeAnalysisContext analysis) => analysis.Node switch
    {
        MethodDeclarationSyntax declaration => (analysis.SemanticModel.GetDeclaredSymbol(declaration, analysis.CancellationToken),
            (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression),
        LocalFunctionStatementSyntax declaration => (analysis.SemanticModel.GetDeclaredSymbol(declaration, analysis.CancellationToken),
            (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression),
        LambdaExpressionSyntax lambda => (analysis.SemanticModel.GetSymbolInfo(lambda, analysis.CancellationToken).Symbol as IMethodSymbol,
            lambda.Body),
        AnonymousMethodExpressionSyntax anonymous => (analysis.SemanticModel.GetSymbolInfo(anonymous, analysis.CancellationToken).Symbol as IMethodSymbol,
            anonymous.Block),
        _ => (null, null),
    };

    private static Frontend GetDeclaredFrontend(IMethodSymbol method, Compilation compilation)
    {
        if (ViewCalls.IsComposable(method))
            return Frontend.Compose;
        if (ViewCalls.IsSolidComponent(method))
            return Frontend.Solid;
        ISymbol? build = compilation.GetTypeByMetadataName("SomeEngine.Views.IView")?.GetMembers("Build").FirstOrDefault();
        if (build is not null && SymbolEqualityComparer.Default.Equals(method,
            method.ContainingType.FindImplementationForInterfaceMember(build)) &&
            method.ContainingType.GetMembers().OfType<IPropertySymbol>().Any(StatePropertyDeclarations.HasStateAttribute))
            return Frontend.SwiftUI;
        return Frontend.None;
    }

    private static Frontend GetPrimitiveFrontend(IMethodSymbol method)
    {
        method = method.ReducedFrom ?? method;
        string type = method.ContainingType.ToDisplayString();
        if (type == "SomeEngine.Views.Compose.Compose" && method.Name is "Remember" or "MutableStateOf")
            return Frontend.Compose;
        if (type == "SomeEngine.Views.Solid.Solid" && method.Name is
            "Effect" or "Memo" or "Show" or "Switch" or "Match" or "For" or "Index" or "Dynamic" or "Insert" or "Untrack")
            return Frontend.Solid;
        if ((type == "SomeEngine.Views.Solid.Solid" && method.Name == "Text" ||
            type == "SomeEngine.Views.Solid.ReactiveModifiers") &&
            method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate))
            return Frontend.Solid;
        return Frontend.None;
    }
}
