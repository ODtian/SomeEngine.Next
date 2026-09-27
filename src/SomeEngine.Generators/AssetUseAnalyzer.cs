using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AssetUseAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor AssetDependency = new("SEAU001", "Use objects must be independent of asset access",
        "Use type '{0}' exposes asset access through '{1}' ({2})", "SomeEngine.Assets", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(AssetDependency);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!Marked(type)) return;
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            foreach (var exposed in Types(member))
            {
                var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                if (FindAsset(exposed, member.Name, visited, out string? path, out ITypeSymbol? dependency))
                    context.ReportDiagnostic(Diagnostic.Create(AssetDependency, member.Locations.FirstOrDefault(),
                        type.Name, path, dependency!.ToDisplayString()));
            }
        }
    }

    private static bool Marked(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (current.GetAttributes().Any(static attr => attr.AttributeClass?.ToDisplayString() == "SomeEngine.Serialization.AssetUseAttribute")) return true;
        return type.AllInterfaces.Any(static value => value.GetAttributes()
            .Any(static attr => attr.AttributeClass?.ToDisplayString() == "SomeEngine.Serialization.AssetUseAttribute"));
    }

    private static IEnumerable<ITypeSymbol> Types(ISymbol member)
    {
        switch (member)
        {
            case IFieldSymbol field: yield return field.Type; break;
            case IPropertySymbol property: yield return property.Type; break;
            case IEventSymbol signal: yield return signal.Type; break;
            case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                yield return method.ReturnType;
                foreach (IParameterSymbol parameter in method.Parameters) yield return parameter.Type;
                break;
        }
    }

    private static bool FindAsset(ITypeSymbol type, string path, HashSet<ITypeSymbol> visited,
        out string? foundPath, out ITypeSymbol? dependency)
    {
        foundPath = null; dependency = null;
        if (!visited.Add(type)) return false;
        if (type is IArrayTypeSymbol array) return FindAsset(array.ElementType, path + "[]", visited, out foundPath, out dependency);
        if (type is not INamedTypeSymbol named) return false;
        for (INamedTypeSymbol? current = named; current is not null; current = current.BaseType)
        {
            string space = current.ContainingNamespace.ToDisplayString();
            if (space == "SomeEngine.Assets" && (current.Name == "Asset" || current.Name == "AssetBlock"
                || current.Name == "AssetId" || current.Name == "AssetRef" || current.Name == "AssetFile"
                || current.Name == "AssetLoader" || current.Name == "AssetBlockSource"))
            { foundPath = path; dependency = type; return true; }
        }
        foreach (ITypeSymbol argument in named.TypeArguments)
            if (FindAsset(argument, path + "<" + argument.Name + ">", visited, out foundPath, out dependency)) return true;
        // Framework storage is described by its generic arguments; do not walk its private implementation graph.
        if (named.SpecialType != SpecialType.None || named.ContainingAssembly.Identity.Name.StartsWith("System")
            || named.ContainingAssembly.Identity.Name == "mscorlib") return false;
        foreach (ISymbol member in named.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member is not IFieldSymbol && member is not IPropertySymbol) continue;
            foreach (ITypeSymbol content in Types(member))
                if (FindAsset(content, path + "." + member.Name, visited, out foundPath, out dependency)) return true;
        }
        if (named.BaseType is not null && FindAsset(named.BaseType, path, visited, out foundPath, out dependency)) return true;
        return false;
    }
}
