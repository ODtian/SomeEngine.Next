using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Coupling complexity gate. A method calling too many distinct types is
/// over-coupled — a sign of patch-stacking (AI kept adding calls instead of
/// extracting an abstraction). Threshold fixed at compile-time-ish; tune
/// via re-grill. Configured at MaxCoupledTypes = 8.
/// </summary>
internal static class CouplingComplexityAnalyzer
{
    public const string RuleId = "SE024";

    public static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
        ImmutableArray.Create(
            Rules.Create(RuleId,
                "Method coupled to too many types",
                "Method '{0}' references {1} distinct types (max {2}). Extract an abstraction or split the method. See harness/config.json#complexity.",
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
        int max = config.Complexity.MaxCoupledTypes;
        var types = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var containingType = ctx.SemanticModel.GetDeclaredSymbol(method)?.ContainingType;

        SyntaxNode? implementation = method.Body ?? (SyntaxNode?)method.ExpressionBody;
        if (implementation is null)
        {
            return;
        }

        foreach (var node in implementation.DescendantNodes())
        {
            CollectReferencedType(ctx, node, containingType, types);
        }

        if (types.Count > max &&
            !AcceptedDiagnosticBaseline.Contains(ctx, config, RuleId, method.Identifier, types.Count))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Descriptors[0],
                method.Identifier.GetLocation(),
                method.Identifier.ValueText,
                types.Count,
                max));
        }
    }

    private static void CollectReferencedType(
        SyntaxNodeAnalysisContext ctx,
        SyntaxNode node,
        ITypeSymbol? containingType,
        HashSet<ITypeSymbol> types)
    {
        ITypeSymbol? type = node switch
        {
            TypeSyntax typeSyntax => ResolveTypeSyntax(ctx, typeSyntax),
            ObjectCreationExpressionSyntax objectCreation => ctx.SemanticModel.GetTypeInfo(objectCreation).Type,
            ImplicitObjectCreationExpressionSyntax implicitCreation => ctx.SemanticModel.GetTypeInfo(implicitCreation).Type,
            InvocationExpressionSyntax invocation => ctx.SemanticModel.GetSymbolInfo(invocation).Symbol?.ContainingType,
            _ => null,
        };

        if (type is { } referencedType && ShouldCount(referencedType, containingType))
        {
            types.Add(NormalizeType(referencedType));
        }
    }

    private static ITypeSymbol? ResolveTypeSyntax(
        SyntaxNodeAnalysisContext ctx,
        TypeSyntax typeSyntax)
    {
        if (ctx.SemanticModel.GetSymbolInfo(typeSyntax).Symbol is ITypeSymbol symbol)
        {
            return symbol;
        }

        return IsExplicitTypePosition(typeSyntax)
            ? ctx.SemanticModel.GetTypeInfo(typeSyntax).Type
            : null;
    }

    private static bool IsExplicitTypePosition(TypeSyntax typeSyntax)
        => typeSyntax.Parent switch
        {
            VariableDeclarationSyntax variable => variable.Type == typeSyntax,
            ObjectCreationExpressionSyntax creation => creation.Type == typeSyntax,
            ArrayCreationExpressionSyntax array => array.Type == typeSyntax,
            CastExpressionSyntax cast => cast.Type == typeSyntax,
            DefaultExpressionSyntax defaultExpression => defaultExpression.Type == typeSyntax,
            TypeOfExpressionSyntax typeOf => typeOf.Type == typeSyntax,
            SizeOfExpressionSyntax sizeOf => sizeOf.Type == typeSyntax,
            _ => false,
        };

    private static bool ShouldCount(ITypeSymbol type, ITypeSymbol? containingType)
    {
        ITypeSymbol normalized = NormalizeType(type);
        if (normalized is ITypeParameterSymbol)
        {
            return false;
        }

        if (containingType is not null && IsInsideOwner(normalized, containingType))
        {
            return false;
        }

        if (normalized.SpecialType != SpecialType.None)
        {
            return false;
        }

        string namespaceName = normalized.ContainingNamespace?.ToDisplayString() ?? "";
        return !namespaceName.StartsWith("System", System.StringComparison.Ordinal)
               && !namespaceName.StartsWith("Microsoft", System.StringComparison.Ordinal)
               && !namespaceName.StartsWith("FlatSharp", System.StringComparison.Ordinal)
               && !namespaceName.StartsWith("SharpGLTF", System.StringComparison.Ordinal)
               && !namespaceName.StartsWith("Alimer", System.StringComparison.Ordinal)
               && !namespaceName.StartsWith("SlangShaderSharp", System.StringComparison.Ordinal);
    }

    private static bool IsInsideOwner(ITypeSymbol type, ITypeSymbol containingType)
    {
        for (ITypeSymbol? currentType = type; currentType is not null; currentType = currentType.ContainingType)
        {
            for (ITypeSymbol? currentOwner = containingType; currentOwner is not null; currentOwner = currentOwner.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(currentType.OriginalDefinition, currentOwner.OriginalDefinition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ITypeSymbol NormalizeType(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => NormalizeType(array.ElementType),
            IPointerTypeSymbol pointer => NormalizeType(pointer.PointedAtType),
            INamedTypeSymbol { IsGenericType: true, ConstructedFrom: { } constructedFrom } => constructedFrom,
            _ => type,
        };
}
