using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SomeEngine.Generators;

internal static class SharedValueTypes
{
    internal const string AttributeName = "SomeEngine.Serialization.BinaryValueAttribute";
    internal static bool IsShareable(ITypeSymbol type, out string reason)
        => IsShareable(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out reason);

    private static bool IsShareable(ITypeSymbol type, HashSet<ITypeSymbol> visited, out string reason)
    {
        reason = "";
        if (type.IsUnmanagedType || type.SpecialType == SpecialType.System_String) return true;
        if (!visited.Add(type)) { reason = "cyclic reference values require explicit asset references"; return false; }
        try
        {
            if (type is not INamedTypeSymbol named) { reason = "use a paged IList for mutable arrays"; return false; }
            if (named.ContainingNamespace.ToDisplayString() == "System"
                && named.Name is "Memory" or "ReadOnlyMemory" or "Span" or "ReadOnlySpan" or "ArraySegment")
            { reason = "memory wrappers can alias mutable storage; use chunks or immutable values"; return false; }
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return IsShareable(named.TypeArguments[0], visited, out reason);
            if (named.ContainingNamespace.ToDisplayString() == "System.Collections.Immutable"
                && named.Name is "ImmutableList" or "ImmutableArray" or "ImmutableDictionary" or "ImmutableHashSet")
            {
                foreach (ITypeSymbol argument in named.TypeArguments)
                    if (!IsShareable(argument, visited, out reason)) return false;
                return true;
            }
            if (named.DeclaringSyntaxReferences.Length == 0)
            {
                if (named.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == AttributeName)) return true;
                reason = "external reference values need BinaryValue proof from their defining assembly";
                return false;
            }
            if (named.TypeKind == TypeKind.Class && !named.IsSealed)
            { reason = "reference values must be sealed to exclude mutable derived objects"; return false; }
            if (named.TypeKind != TypeKind.Class && named.TypeKind != TypeKind.Struct)
            { reason = "this reference shape has no proven value semantics"; return false; }
            for (INamedTypeSymbol? current = named; current is not null
                && current.SpecialType is not SpecialType.System_Object and not SpecialType.System_ValueType; current = current.BaseType)
            {
                if (current.DeclaringSyntaxReferences.Length == 0)
                    return IsShareable(current, visited, out reason);
                if (current.GetMembers().OfType<IEventSymbol>().Any(item => !item.IsStatic))
                { reason = "instance events retain mutable subscription state"; return false; }
                foreach (IFieldSymbol field in current.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic))
                {
                    if (field.AssociatedSymbol is IPropertySymbol) continue;
                    if (current.TypeKind == TypeKind.Class && !field.IsReadOnly)
                    { reason = "field '" + field.Name + "' is mutable"; return false; }
                    if (!IsShareable(field.Type, visited, out reason)) return false;
                }
                foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic && !property.IsIndexer))
                {
                    if (current.TypeKind == TypeKind.Class && property.SetMethod is { IsInitOnly: false })
                    { reason = "property '" + property.Name + "' has a mutable setter"; return false; }
                    if (property.IsImplicitlyDeclared) continue;
                    if (!IsShareable(property.Type, visited, out reason)) return false;
                }
            }
            return true;
        }
        finally { visited.Remove(type); }
    }
}
