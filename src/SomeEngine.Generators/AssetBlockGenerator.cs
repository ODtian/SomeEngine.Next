using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

[Generator]
public sealed class AssetBlockGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidBlock = new("SEAB001", "Invalid asset block declaration",
        "Asset block '{0}': {1}", "SomeEngine.Assets", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor MutableValue = new("SEAB002", "Asset block value cannot be shared",
        "Asset block property '{0}' cannot share '{1}': {2}", "SomeEngine.Assets", DiagnosticSeverity.Error, true);
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
    private static readonly SymbolDisplayFormat NameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.ForAttributeWithMetadataName("SomeEngine.Assets.AssetBlockAttribute",
            static (syntax, _) => syntax is ClassDeclarationSyntax,
            static (attribute, _) => (INamedTypeSymbol)attribute.TargetSymbol);
        context.RegisterSourceOutput(types, static (output, type) => Generate(output, type));
    }

    private static void Generate(SourceProductionContext output, INamedTypeSymbol type)
    {
        void Error(string reason) => output.ReportDiagnostic(Diagnostic.Create(InvalidBlock,
            type.Locations.FirstOrDefault(), type.Name, reason));
        if (type.Arity != 0 || type.ContainingType is not null || type.IsRecord || type.IsAbstract
            || !DerivesFrom(type, "SomeEngine.Assets.AssetBlock")
            || type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is ClassDeclarationSyntax declaration
                && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            Error("use a non-generic top-level partial class derived from AssetBlock");
            return;
        }
        AttributeData blockAttribute = Attribute(type, "SomeEngine.Assets.AssetBlockAttribute")!;
        string? declaredName = blockAttribute.ConstructorArguments.Length == 0
            ? null : blockAttribute.ConstructorArguments[0].Value as string;
        if (string.IsNullOrWhiteSpace(declaredName))
        { Error("provide a persistent block name containing non-whitespace text"); return; }
        string blockName = declaredName!;
        string blockId = ComputeBlockId(blockName);

        bool binary = Attribute(type, "SomeEngine.Serialization.BinarySerializableAttribute") is not null;
        bool generatedBinary = binary && Attribute(type, "SomeEngine.Serialization.BinaryFieldsAttribute") is not null;
        if (!generatedBinary)
        {
            output.AddSource(type.ToDisplayString().Replace('.', '_') + ".AssetBlock.g.cs", EmitIdentity(type, blockId));
            return;
        }

        var fields = type.GetMembers().OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic && property.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is PropertyDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        bool invalid = false;
        foreach (IPropertySymbol property in fields)
        {
            if (property.IsIndexer || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                || property.SetMethod?.DeclaredAccessibility != Accessibility.Public || property.SetMethod.IsInitOnly)
            { Error("partial content properties require public get and set accessors"); invalid = true; continue; }
            ITypeSymbol valueType = ElementType(property.Type) ?? property.Type;
            if (!SharedValueTypes.IsShareable(valueType, out string reason))
            {
                output.ReportDiagnostic(Diagnostic.Create(MutableValue, property.Locations.FirstOrDefault(),
                    property.Name, valueType.ToDisplayString(), reason));
                invalid = true;
            }
        }
        if (invalid) return;
        output.AddSource(type.ToDisplayString().Replace('.', '_') + ".AssetBlock.g.cs", Emit(type, fields, blockId));
    }

    private static string EmitIdentity(INamedTypeSymbol type, string blockId)
    {
        string fullType = type.ToDisplayString(TypeFormat);
        var code = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!type.ContainingNamespace.IsGlobalNamespace)
            code.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
        code.Append(type.DeclaredAccessibility == Accessibility.Public ? "public " : "internal ")
            .Append("partial class @").Append(type.Name).Append(" : global::SomeEngine.Assets.IAssetBlock<")
            .Append(fullType).AppendLine("> {");
        code.Append("public static global::System.Guid BlockTypeId { get; } = new global::System.Guid(")
            .Append(Literal(blockId)).AppendLine(");");
        code.AppendLine("[global::SomeEngine.Serialization.BinaryIgnore] public override global::System.Guid BlockId => BlockTypeId;");
        code.AppendLine("}");
        return code.ToString();
    }

    private static string Emit(INamedTypeSymbol type, IPropertySymbol[] fields, string blockId)
    {
        string fullType = type.ToDisplayString(TypeFormat);
        string name = "@" + type.Name;
        var code = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!type.ContainingNamespace.IsGlobalNamespace)
            code.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
        code.Append(type.DeclaredAccessibility == Accessibility.Public ? "public " : "internal ")
            .Append("partial class ").Append(name).Append(" : global::SomeEngine.Assets.IAssetBlock<")
            .Append(fullType).AppendLine(">, global::SomeEngine.Assets.IAssetBlockWriter {");
        code.Append("public static global::System.Guid BlockTypeId { get; } = new global::System.Guid(")
            .Append(Literal(blockId)).AppendLine(");");
        code.AppendLine("[global::SomeEngine.Serialization.BinaryIgnore] public override global::System.Guid BlockId => BlockTypeId;");
        foreach (IPropertySymbol field in fields)
        {
            string fieldType = field.Type.ToDisplayString(TypeFormat);
            string initial = ElementType(field.Type) is not null ? "[]"
                : field.Type.SpecialType == SpecialType.System_String ? "string.Empty" : "default!";
            if (field.NullableAnnotation == NullableAnnotation.Annotated) initial = "default";
            code.Append("private ").Append(fieldType).Append(" _").Append(field.Name).Append(" = ").Append(initial).AppendLine(";");
        }
        code.Append("public ").Append(name).AppendLine("() { Initialize(); }");
        code.AppendLine("partial void Initialize();");
        code.Append("partial void CopyFrom(").Append(fullType).AppendLine(" source);");
        code.Append(type.IsSealed ? "private " : "protected ").Append(name).Append('(').Append(fullType)
            .AppendLine(" source, object? syncRoot = null) : base(source, syncRoot) {");
        foreach (IPropertySymbol field in fields)
        {
            code.Append('_').Append(field.Name).Append(" = source._").Append(field.Name);
            if (ElementType(field.Type) is { } element)
                code.Append(" is { } values").Append(field.Name).Append(" ? new global::System.Collections.Generic.List<")
                    .Append(element.ToDisplayString(TypeFormat)).Append(">(values").Append(field.Name).Append(") : null!");
            code.AppendLine(";");
        }
        code.AppendLine("CopyFrom(source); }");
        code.Append("public override ").Append(fullType).AppendLine(" Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }");
        code.Append("public override ").Append(fullType).AppendLine(" Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }");
        foreach (IPropertySymbol field in fields)
        {
            code.Append("public partial ").Append(field.Type.ToDisplayString(TypeFormat)).Append(" @").Append(field.Name).AppendLine(" {");
            code.Append("get { lock (SyncRoot) { RequireIdle(); return _").Append(field.Name).AppendLine("; } }");
            code.Append("set { lock (SyncRoot) { RequireIdle(); _").Append(field.Name).Append(" = ");
            if (ElementType(field.Type) is { } element)
                code.Append("value is null ? null! : new global::System.Collections.Generic.List<")
                    .Append(element.ToDisplayString(TypeFormat)).Append(">(value)");
            else code.Append("value");
            code.AppendLine("; MarkChanged(); } } }");
        }

        code.AppendLine("public override global::System.Collections.Generic.IEnumerable<global::System.Guid> References() {");
        int referenceIndex = 0;
        foreach (IPropertySymbol field in fields)
            EmitReferences(code, field.Type, "@" + field.Name,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), ref referenceIndex);
        code.AppendLine("yield break; }");
        code.Append("public static async global::System.Threading.Tasks.ValueTask<").Append(fullType)
            .AppendLine("> ReadAsync(global::SomeEngine.Assets.AssetBlockSource source, global::System.Threading.CancellationToken cancellationToken = default) {");
        code.Append("var result = new ").Append(name).AppendLine("();");
        code.AppendLine("try { await source.ReadIntoAsync(result, cancellationToken).ConfigureAwait(false); result.AttachSource(source); return result; } catch (global::System.Exception error) { try { result.Dispose(); } catch (global::System.Exception cleanupError) { throw new global::System.AggregateException(error, cleanupError); } throw; } }");
        if (!type.GetMembers("WriteAsync").OfType<IMethodSymbol>().Any(method => method.Parameters.Length != 0
            && method.Parameters[0].Type.ToDisplayString() == "SomeEngine.Assets.AssetBlockWriter"))
        {
            code.AppendLine("async global::System.Threading.Tasks.ValueTask global::SomeEngine.Assets.IAssetBlockWriter.WriteAsync(global::SomeEngine.Assets.AssetBlockWriter writer, global::System.Threading.CancellationToken cancellationToken) {");
            code.AppendLine("writer.WriteMetadata(BlockTypeId, this); await WriteChunksAsync(writer, cancellationToken).ConfigureAwait(false); }");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    private static void EmitReferences(StringBuilder code, ITypeSymbol type, string value,
        HashSet<ITypeSymbol> visited, ref int index)
    {
        if (type is not INamedTypeSymbol named) return;
        if (named.Name == "AssetId" && named.ContainingNamespace.ToDisplayString() == "SomeEngine.Assets")
        { code.Append("yield return ").Append(value).AppendLine(".Value;"); return; }
        if (named.ToDisplayString() == "System.Guid" || (type.SpecialType != SpecialType.None
            && named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
            || type.TypeKind == TypeKind.Enum || !visited.Add(type)) return;
        string local = "reference" + index++;
        var body = new StringBuilder();
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            EmitReferences(body, named.TypeArguments[0], local, visited, ref index);
            if (body.Length != 0) code.Append("if (").Append(value).Append(" is { } ").Append(local)
                .AppendLine(") {").Append(body).AppendLine("}");
            visited.Remove(type);
            return;
        }
        var enumerable = named.AllInterfaces.FirstOrDefault(item => item.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        if (enumerable is not null)
        {
            EmitReferences(body, enumerable.TypeArguments[0], local, visited, ref index);
            if (body.Length != 0) code.Append("if (").Append(value).Append(" is { } collection").Append(local)
                .Append(") foreach (var ").Append(local).Append(" in collection").Append(local).AppendLine(") {").Append(body).AppendLine("}");
        }
        else
        {
            for (INamedTypeSymbol? current = named; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                foreach (ISymbol member in current.GetMembers().Where(member => !member.IsStatic
                    && member.DeclaredAccessibility == Accessibility.Public
                    && Attribute(member, "SomeEngine.Serialization.BinaryIgnoreAttribute") is null))
                {
                    ITypeSymbol? memberType = member is IPropertySymbol { IsIndexer: false } property ? property.Type
                        : member is IFieldSymbol field ? field.Type : null;
                    if (memberType is not null)
                        EmitReferences(body, memberType, local + ".@" + member.Name, visited, ref index);
                }
            if (body.Length != 0) code.Append("if (").Append(value).Append(" is { } ").Append(local).AppendLine(") {").Append(body).AppendLine("}");
        }
        visited.Remove(type);
    }

    private static ITypeSymbol? ElementType(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IList<T>" ? named.TypeArguments[0] : null;
    private static AttributeData? Attribute(ISymbol type, string name)
        => type.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);

    private static bool DerivesFrom(INamedTypeSymbol type, string fullName)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == fullName) return true;
        return false;
    }

    private static string ComputeBlockId(string name)
    {
        byte[] hash;
        using (SHA256 sha = SHA256.Create())
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes("SomeEngine.AssetBlock.v1:" + name));
        StringBuilder value = new(36);
        for (int index = 0; index < 16; index++)
        {
            if (index == 4 || index == 6 || index == 8 || index == 10) value.Append('-');
            value.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
        }
        return value.ToString();
    }

    private static string Literal(string value)
        => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
