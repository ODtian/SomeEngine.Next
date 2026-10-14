using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

/// <summary>Generates dense, material-local slots for partial properties on Material types.</summary>
[Generator]
public sealed class MaterialGen : IIncrementalGenerator
{
    private const string MaterialTypeName = "SomeEngine.Resources.Material";

    private static readonly SymbolDisplayFormat QualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (production, compilation) =>
            {
                foreach (MaterialInfo material in Build(compilation))
                    production.AddSource(material.HintName, Emit(material));
            });
    }

    private static ImmutableArray<MaterialInfo> Build(Compilation compilation)
    {
        INamedTypeSymbol? materialBase = compilation.GetTypeByMetadataName(MaterialTypeName);
        if (materialBase is null)
            return [];

        var materials = ImmutableArray.CreateBuilder<MaterialInfo>();
        Collect(compilation.Assembly.GlobalNamespace, materialBase, materials, compilation);
        return materials.ToImmutable();
    }

    private static void Collect(
        INamespaceSymbol ns,
        INamedTypeSymbol materialBase,
        ImmutableArray<MaterialInfo>.Builder materials, Compilation compilation)
    {
        foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            Collect(child, materialBase, materials, compilation);
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
            Collect(type, materialBase, materials, compilation);
    }

    private static void Collect(
        INamedTypeSymbol type,
        INamedTypeSymbol materialBase,
        ImmutableArray<MaterialInfo>.Builder materials, Compilation compilation)
    {
        if (IsMaterial(type, materialBase))
        {
            var hierarchy = new List<INamedTypeSymbol>();
            for (INamedTypeSymbol? current = type;
                 current is not null && !SymbolEqualityComparer.Default.Equals(current, materialBase);
                 current = current.BaseType)
                hierarchy.Add(current);
            hierarchy.Reverse();
            IPropertySymbol[] propertySymbols = hierarchy
                .SelectMany(static current => current.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(static property => !property.IsStatic && IsPartialProperty(property))
                    .OrderBy(static property => SourcePath(property), StringComparer.Ordinal)
                    .ThenBy(static property => SourceStart(property))
                    .ThenBy(static property => property.Name, StringComparer.Ordinal))
                .ToArray();
            int ownPropertyCount = propertySymbols.Count(property =>
                SymbolEqualityComparer.Default.Equals(property.ContainingType, type));
            ImmutableArray<PropertyInfo> properties = propertySymbols
                .Select((property, slot) => new PropertyInfo(
                    property.Name,
                    property.Type.ToDisplayString(QualifiedTypeFormat),
                    property.Type.WithNullableAnnotation(NullableAnnotation.None)
                        .ToDisplayString(QualifiedTypeFormat),
                    AccessibilityText(property.DeclaredAccessibility),
                    property.SetMethod?.IsInitOnly == true,
                    checked((uint)slot), DefaultExpression(property, compilation)))
                .ToImmutableArray();
            if (!properties.IsDefaultOrEmpty)
            {
                materials.Add(new MaterialInfo(
                    type.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : type.ContainingNamespace.ToDisplayString(),
                    AccessibilityText(type.DeclaredAccessibility),
                    type.IsSealed,
                    type.IsAbstract,
                    type.Name,
                    type.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Resources.MaterialTypeAttribute")?.ConstructorArguments.FirstOrDefault().Value as string ?? type.ToDisplayString(),
                    Sanitize(type.ToDisplayString()) + ".Material.g.cs",
                    ownPropertyCount,
                    properties));
            }
        }

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            Collect(nested, materialBase, materials, compilation);
    }

    private static bool IsMaterial(INamedTypeSymbol type, INamedTypeSymbol materialBase)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, materialBase))
                return true;
        return false;
    }

    private static bool IsPartialProperty(IPropertySymbol property)
        => property.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is PropertyDeclarationSyntax syntax &&
            syntax.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static string Emit(MaterialInfo material)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        if (material.Namespace.Length != 0) source.Append("namespace ").Append(material.Namespace).AppendLine(";");
        source.Append(material.Accessibility).Append(" partial class ").Append(material.Name);
        if (!material.IsAbstract)
            source.Append(" : global::SomeEngine.Resources.IMaterial<").Append(material.Name).Append('>');
        source.AppendLine(" {");
        foreach (PropertyInfo p in material.Properties.Skip(material.Properties.Length - material.OwnPropertyCount))
        {
            source.Append(p.Accessibility).Append(" partial ").Append(p.Type).Append(' ').Append(p.Name)
                .Append(" { get => field; ").Append(p.IsInitOnly ? "init" : "set")
                .Append(" => SetProperty(ref field, value, ").Append(p.Slot).AppendLine("u); }");
        }
        string name = material.Namespace.Length == 0 ? material.Name : material.Namespace + "." + material.Name;
        source.Append(material.Properties.Length > material.OwnPropertyCount ? "public new static " : "public static ")
            .Append("global::System.Guid TypeId { get; } = global::SomeEngine.Serialization.BinaryTypeId.FromLogicalName(\"")
            .Append(material.TypeName).AppendLine("\");");
        source.AppendLine("public override global::System.Guid MaterialTypeId => TypeId;");
        source.AppendLine("private static readonly global::SomeEngine.Resources.MaterialPropertyDefinition[] Definitions = [");
        foreach (PropertyInfo p in material.Properties)
            source.Append("new(").Append(p.Slot).Append("u, \"").Append(Escape(p.Name)).Append("\", typeof(").Append(p.TypeOf).AppendLine(")),");
        source.AppendLine("];");
        source.AppendLine("public override global::System.Collections.Generic.IReadOnlyList<global::SomeEngine.Resources.MaterialPropertyDefinition> Properties => Definitions;");
        source.AppendLine("public override global::SomeEngine.Graphics.DeviceResource? GetDeviceResource(uint index) => index switch {");
        foreach (PropertyInfo p in material.Properties)
            if (p.TypeOf.EndsWith(".BufferSrv", StringComparison.Ordinal) || p.TypeOf.EndsWith(".BufferUav", StringComparison.Ordinal) ||
                p.TypeOf.EndsWith(".BufferCbv", StringComparison.Ordinal) || p.TypeOf.EndsWith(".AccelerationStructureSrv", StringComparison.Ordinal))
                source.Append(p.Slot).Append("u => ").Append(p.Name).AppendLine(",");
        source.AppendLine("_ => null, };");
        EmitGetter("global::SomeEngine.Resources.Texture?", "GetTexture", "Texture", "null");
        EmitGetter("global::SomeEngine.Graphics.SamplerDesc", "GetSampler", "Sampler", "default");
        source.AppendLine("public override global::System.Numerics.Vector4 GetFloatComponents(uint index) => index switch {");
        foreach (PropertyInfo p in material.Properties)
        {
            string kind = Kind(p);
            string? expression = kind == "Float" ? "new(" + p.Name + ")"
                : kind == "Vector2" ? "new(" + p.Name + ", 0f, 0f)"
                : kind == "Vector3" ? "new(" + p.Name + ", 0f)"
                : kind == "Vector4" ? p.Name : null;
            if (expression != null) source.Append(p.Slot).Append("u => ").Append(expression).AppendLine(",");
        }
        source.AppendLine("_ => default, };");
        source.AppendLine("public override uint GetInteger(uint index) => index switch {");
        foreach (PropertyInfo p in material.Properties)
        {
            string kind = Kind(p);
            string? expression = kind == "Int" ? "unchecked((uint)" + p.Name + ")"
                : kind == "UInt" ? p.Name : kind == "Bool" ? p.Name + " ? 1u : 0u" : null;
            if (expression != null) source.Append(p.Slot).Append("u => ").Append(expression).AppendLine(",");
        }
        source.AppendLine("_ => 0, };");
        source.AppendLine("public override void ApplyParameter(global::SomeEngine.Resources.MaterialValue parameter, global::SomeEngine.Resources.Texture? texture) {");
        source.AppendLine("switch (parameter.Name) {");
        foreach (PropertyInfo p in material.Properties)
        {
            string kind = Kind(p);
            if (kind == "") continue;
            source.Append("case \"").Append(Escape(p.Name)).Append("\" when parameter.Kind == global::SomeEngine.Resources.MaterialParameterKind.")
                .Append(kind).Append(": ").Append(p.Name).Append(" = ")
                .Append(kind == "Texture" ? "texture" : "parameter." + kind).AppendLine("; break;");
        }
        source.AppendLine("default: throw new global::System.IO.InvalidDataException($\"Material parameter '{parameter.Name}' does not match the concrete properties.\"); } }");
        if (!material.IsAbstract)
        {
            source.Append("static ").Append(material.Name).Append(" global::SomeEngine.Resources.IMaterial<")
                .Append(material.Name).AppendLine(">.Create(string? name, global::SomeEngine.Resources.Shader? shader, global::System.Collections.Generic.IReadOnlyList<(global::SomeEngine.Resources.MaterialValue Value, global::SomeEngine.Resources.Texture? Texture)> values) {");
            source.Append("var result = new ").Append(material.Name).AppendLine("();");
            source.AppendLine("result.InitializeContent(name, shader, values);");
            source.AppendLine("return result; }");
            source.Append("protected override global::SomeEngine.Resources.Material CreateEmpty() => new ").Append(material.Name).AppendLine("();");
        }
        source.AppendLine("protected override void CopyProperties(global::SomeEngine.Resources.Material source) {");
        source.Append("var value = (").Append(material.Name).AppendLine(")source;");
        foreach (PropertyInfo p in material.Properties)
            source.Append(p.Name).Append(" = value.").Append(p.Name).AppendLine(";");
        source.AppendLine("}");
        source.AppendLine("}");
        return source.ToString();

        void EmitGetter(string type, string method, string kind, string fallback)
        {
            source.Append("public override ").Append(type).Append(' ').Append(method).AppendLine("(uint index) => index switch {");
            foreach (PropertyInfo p in material.Properties)
                if (Kind(p) == kind) source.Append(p.Slot).Append("u => ").Append(p.Name).AppendLine(",");
            source.Append("_ => ").Append(fallback).AppendLine(", };");
        }
    }

    internal static string DefaultExpression(IPropertySymbol property, Compilation compilation)
    {
        foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer: { } initializer })
                return new QualifiedNames(compilation.GetSemanticModel(initializer.SyntaxTree)).Visit(initializer.Value)!.ToString();
        return "default";
    }

    private sealed class QualifiedNames(SemanticModel semantics) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            ISymbol? symbol = semantics.GetSymbolInfo(node).Symbol;
            if (symbol is INamedTypeSymbol type)
                return SyntaxFactory.ParseName(type.ToDisplayString(QualifiedTypeFormat)).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            ISymbol? symbol = semantics.GetSymbolInfo(node).Symbol;
            if (symbol is { IsStatic: true, ContainingType: { } type })
                return SyntaxFactory.ParseExpression(type.ToDisplayString(QualifiedTypeFormat) + "." + symbol.Name).WithTriviaFrom(node);
            return base.VisitMemberAccessExpression(node);
        }
    }

    private static string Kind(PropertyInfo property)
    {
        string type = property.TypeOf;
        if (type.EndsWith(".Texture", StringComparison.Ordinal)) return "Texture";
        if (type.EndsWith(".SamplerDesc", StringComparison.Ordinal)) return "Sampler";
        if (type == "float") return "Float";
        if (type == "int") return "Int";
        if (type == "uint") return "UInt";
        if (type == "bool") return "Bool";
        foreach (string name in new[] { "Vector2", "Vector3", "Vector4", "Matrix4x4" })
            if (type.EndsWith("." + name, StringComparison.Ordinal)) return name;
        return "";
    }

    private static string SourcePath(IPropertySymbol property)
        => property.Locations.FirstOrDefault(static location => location.IsInSource)
            ?.SourceTree?.FilePath ?? string.Empty;

    private static int SourceStart(IPropertySymbol property)
        => property.Locations.FirstOrDefault(static location => location.IsInSource)
            ?.SourceSpan.Start ?? int.MaxValue;

    private static string AccessibilityText(Accessibility accessibility)
        => accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "private",
        };

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            result.Append(char.IsLetterOrDigit(character) ? character : '_');
        return result.ToString();
    }

    private readonly struct MaterialInfo
    {
        internal MaterialInfo(
            string ns,
            string accessibility,
            bool isSealed,
            bool isAbstract,
            string name,
            string typeName,
            string hintName,
            int ownPropertyCount,
            ImmutableArray<PropertyInfo> properties)
        {
            Namespace = ns;
            Accessibility = accessibility;
            IsSealed = isSealed;
            IsAbstract = isAbstract;
            Name = name;
            TypeName = typeName;
            HintName = hintName;
            OwnPropertyCount = ownPropertyCount;
            Properties = properties;
        }

        internal string Namespace { get; }
        internal string Accessibility { get; }
        internal bool IsSealed { get; }
        internal bool IsAbstract { get; }
        internal string Name { get; }
        internal string TypeName { get; }
        internal string HintName { get; }
        internal int OwnPropertyCount { get; }
        internal ImmutableArray<PropertyInfo> Properties { get; }
    }

    private readonly struct PropertyInfo
    {
        internal PropertyInfo(
            string name,
            string type,
            string typeOf,
            string accessibility,
            bool isInitOnly,
            uint slot, string defaultValue)
        {
            Name = name;
            Type = type;
            TypeOf = typeOf;
            Accessibility = accessibility;
            IsInitOnly = isInitOnly;
            Slot = slot;
            DefaultValue = defaultValue;
        }

        internal string Name { get; }
        internal string Type { get; }
        internal string TypeOf { get; }
        internal string Accessibility { get; }
        internal bool IsInitOnly { get; }
        internal uint Slot { get; }
        internal string DefaultValue { get; }
    }
}
