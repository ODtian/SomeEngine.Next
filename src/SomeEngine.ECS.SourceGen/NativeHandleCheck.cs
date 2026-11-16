using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

/// <summary>
/// Reports component and buffer element types that hold pointers, native-sized handles or fixed buffers without
/// declaring <c>SomeEngine.ECS.INativeHandleValue</c>. At run time a Job only sees managed references and that
/// declaration, so an undeclared handle would let a Job borrow the value by reference.
/// </summary>
[Generator]
public sealed class NativeHandleCheck : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Undeclared = new("ECSG003", "Native handles must be declared",
        "{0} holds pointers, native-sized handles or fixed buffers; declare SomeEngine.ECS.INativeHandleValue " +
        "so Jobs do not borrow it by reference", "ECS", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var uses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax or
                    GenericNameSyntax or InvocationExpressionSyntax,
                static (syntax, _) => GetUses(syntax))
            .Collect().Combine(context.CompilationProvider);
        context.RegisterSourceOutput(uses, static (output, input) => Report(output, input.Left, input.Right));
    }

    private static ImmutableArray<(INamedTypeSymbol Type, Location Location)> GetUses(GeneratorSyntaxContext context)
    {
        ISymbol? symbol = context.Node is StructDeclarationSyntax or RecordDeclarationSyntax
            ? context.SemanticModel.GetDeclaredSymbol(context.Node)
            : context.SemanticModel.GetSymbolInfo(context.Node).Symbol;
        Location location = context.Node.GetLocation();
        IEnumerable<INamedTypeSymbol> types = symbol switch
        {
            INamedTypeSymbol type => [type],
            IMethodSymbol method => method.TypeArguments.OfType<INamedTypeSymbol>(),
            _ => [],
        };
        return types.Select(type => (type, location)).ToImmutableArray();
    }

    private static void Report(SourceProductionContext output,
        ImmutableArray<ImmutableArray<(INamedTypeSymbol Type, Location Location)>> inputs, Compilation compilation)
    {
        INamedTypeSymbol? declaration = compilation.GetTypeByMetadataName("SomeEngine.ECS.INativeHandleValue");
        if (declaration is null)
            return;
        var analysis = new JobStorageTypeAnalysis(compilation);
        var checkedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var input in inputs)
            foreach (var (type, location) in input)
                Check(type, location);

        void Check(INamedTypeSymbol type, Location location)
        {
            foreach (var argument in type.TypeArguments.OfType<INamedTypeSymbol>())
                Check(argument, location);
            if (ContainsTypeParameter(type) || !checkedTypes.Add(type))
                return;
            if (!type.IsValueType || type.IsRefLikeType || !type.AllInterfaces.Any(IsStorage) || !type.IsUnmanagedType)
                return;
            if (analysis.IsAliasFree(type) || type.AllInterfaces.Contains(declaration, SymbolEqualityComparer.Default))
                return;
            Location at = type.Locations.FirstOrDefault(value => value.IsInSource) ?? location;
            output.ReportDiagnostic(Diagnostic.Create(Undeclared, at, type.ToDisplayString()));
        }
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type is ITypeParameterSymbol ||
        type is INamedTypeSymbol named && (named.TypeArguments.Any(ContainsTypeParameter) ||
            named.ContainingType is not null && ContainsTypeParameter(named.ContainingType));

    private static bool IsStorage(INamedTypeSymbol type) => type.ToDisplayString() is
        "SomeEngine.ECS.IComponent" or "SomeEngine.ECS.ITagComponent" or "SomeEngine.ECS.ISparseComponent" or
        "SomeEngine.ECS.IBufferElement" or "SomeEngine.ECS.ISharedComponent" or "SomeEngine.ECS.Components.ITag" or
        "SomeEngine.ECS.Components.ISparseComponent" or "SomeEngine.ECS.Components.IBufferElement" or
        "SomeEngine.ECS.Components.ISharedComponent";
}
