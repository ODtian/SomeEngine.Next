using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SomeEngine.ECS.SourceGen;

internal sealed class JobStorageTypeAnalysis
{
    private readonly Compilation _allFields;

    internal JobStorageTypeAnalysis(Compilation compilation)
    {
        _allFields = ((CSharpCompilation)compilation).WithOptions(
            ((CSharpCompilationOptions)compilation.Options).WithMetadataImportOptions(MetadataImportOptions.All));
    }

    internal bool IsAliasFree(ITypeSymbol type) =>
        IsAliasFree(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private bool IsAliasFree(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!type.IsUnmanagedType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
            return false;
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            return false;
        if (type is ITypeParameterSymbol)
            return true;
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Enum)
            return true;
        if (IsFrameworkHandle(named))
            return false;
        named = WithAllFields(named);
        if (!seen.Add(named))
            return true;
        foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            if (field.IsFixedSizeBuffer || !IsAliasFree(field.Type, seen))
                return false;
        }
        return true;
    }

    // Reference assemblies replace these structs' private handle fields with placeholders, so their fields cannot
    // show that they hold a native-sized handle.
    private static bool IsFrameworkHandle(INamedTypeSymbol type) => type.ContainingNamespace?.ToDisplayString() switch
    {
        "System" => type.Name is "RuntimeTypeHandle" or "RuntimeMethodHandle" or "RuntimeFieldHandle" or "ModuleHandle",
        "System.Runtime.InteropServices" => type.Name is "GCHandle" or "PinnedGCHandle" or "WeakGCHandle",
        _ => false,
    };

    private INamedTypeSymbol WithAllFields(INamedTypeSymbol type)
    {
        if (type.Locations.Any(location => location.IsInSource))
            return type;
        var definition = type.ContainingType is { } parent
            ? WithAllFields(parent).GetTypeMembers(type.Name, type.Arity).Single()
            : _allFields.GetTypeByMetadataName(type.ContainingNamespace.IsGlobalNamespace
                ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName)!;
        if (type.Arity == 0)
            return definition;
        var arguments = type.TypeArguments.Select(argument => argument is INamedTypeSymbol named
            ? WithAllFields(named) : argument).ToArray();
        return definition.Construct(arguments);
    }
}
