using System.Text;
using Microsoft.CodeAnalysis;

namespace SomeEngine.ECS.SourceGen;

internal static class RelationTypeSyntax
{
    internal static IEnumerable<ITypeParameterSymbol> TypeParameters(INamedTypeSymbol type) =>
        (type.ContainingType is null ? Enumerable.Empty<ITypeParameterSymbol>() : TypeParameters(type.ContainingType))
            .Concat(type.TypeParameters);

    internal static bool IsPublic(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public && (type.ContainingType is null || IsPublic(type.ContainingType));

    internal static void AppendConstraints(StringBuilder text, ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
        else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
        else if (parameter.HasReferenceTypeConstraint) constraints.Add("class");
        else if (parameter.HasNotNullConstraint) constraints.Add("notnull");
        constraints.AddRange(parameter.ConstraintTypes.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        if (parameter.HasConstructorConstraint) constraints.Add("new()");
        if (constraints.Count > 0)
            text.Append("        where ").Append(parameter.Name).Append(" : ").AppendLine(string.Join(", ", constraints));
    }
}
