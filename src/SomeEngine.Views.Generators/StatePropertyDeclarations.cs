using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Views.Generators;

internal static class StatePropertyDeclarations
{
    internal static bool HasStateAttribute(IPropertySymbol property) => property.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.SwiftUI.StateAttribute");

    internal static bool CanImplement(IPropertySymbol property)
    {
        INamedTypeSymbol type = property.ContainingType;
        if (property.IsStatic || property.IsIndexer || property.ReturnsByRef || property.ReturnsByRefReadonly ||
            property.ExplicitInterfaceImplementations.Length > 0 || type.TypeKind != TypeKind.Struct ||
            !type.AllInterfaces.Any(value => value.ToDisplayString() == "SomeEngine.Views.IView") ||
            !type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            return false;

        return property.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) && !declaration.Modifiers.Any(SyntaxKind.RequiredKeyword) &&
            declaration.AccessorList is { Accessors.Count: 2 } accessors &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null) &&
            accessors.Accessors.Any(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)) &&
            accessors.Accessors.Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
    }
}
