using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

[Generator]
public sealed class RenderGraphParameterGenerator : IIncrementalGenerator
{
    private const string PassParametersAttributeName = "SomeEngine.RenderGraph.PassParametersAttribute";
    private const string ShaderParametersAttributeName = "SomeEngine.RenderGraph.ShaderParametersAttribute";
    private const string BufferParameterName = "global::SomeEngine.RenderGraph.BufferParameter";
    private const string BufferParameterArrayName = "global::SomeEngine.RenderGraph.BufferParameterArray";
    private const string TextureParameterName = "global::SomeEngine.RenderGraph.TextureParameter";
    private const string TextureParameterArrayName = "global::SomeEngine.RenderGraph.TextureParameterArray";
    private const string SamplerParameterName = "global::SomeEngine.RenderGraph.SamplerParameter";
    private const string SamplerParameterArrayName = "global::SomeEngine.RenderGraph.SamplerParameterArray";
    private const string ConstantParameterName = "global::SomeEngine.RenderGraph.ConstantParameter<T>";

    private static readonly DiagnosticDescriptor InvalidShape = new(
        "SERG001",
        "RenderGraph parameter shape cannot be generated",
        "Parameter type '{0}' must be a top-level, non-generic, non-readonly partial struct",
        "SomeEngine.RenderGraph",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        "SERG002",
        "RenderGraph parameter member is unsupported",
        "Parameter member '{0}' must be a field of BufferParameter, BufferParameterArray, TextureParameter, TextureParameterArray, SamplerParameter, SamplerParameterArray, or ConstantParameter<T>",
        "SomeEngine.RenderGraph",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (productionContext, compilation) => Generate(productionContext, compilation));
    }

    private static void Generate(SourceProductionContext context, Compilation compilation)
    {
        foreach (INamedTypeSymbol type in FindParameterTypes(compilation.Assembly.GlobalNamespace))
        {
            if (!IsSupportedShape(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidShape,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString()));
                continue;
            }

            ImmutableArray<IFieldSymbol> fields = type.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
                .OrderBy(static field => field.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(static field => field.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
                .ToImmutableArray();
            ImmutableArray<IPropertySymbol> properties = type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(static property => !property.IsStatic && !property.IsImplicitlyDeclared)
                .ToImmutableArray();
            bool invalid = false;
            foreach (IPropertySymbol property in properties)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMember,
                    property.Locations.FirstOrDefault(),
                    property.Name));
                invalid = true;
            }

            foreach (IFieldSymbol field in fields)
            {
                if (Classify(field.Type) != ParameterFieldKind.Unsupported) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMember,
                    field.Locations.FirstOrDefault(),
                    field.Name));
                invalid = true;
            }
            if (invalid) continue;

            string hint = Sanitize(type.ToDisplayString()) + ".RenderGraphParameters.g.cs";
            context.AddSource(hint, Emit(type, fields));
        }
    }

    private static ImmutableArray<INamedTypeSymbol> FindParameterTypes(INamespaceSymbol root)
    {
        ImmutableArray<INamedTypeSymbol>.Builder result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        Collect(root, result);
        return result
            .OrderBy(static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void Collect(INamespaceSymbol value, ImmutableArray<INamedTypeSymbol>.Builder result)
    {
        foreach (INamespaceSymbol child in value.GetNamespaceMembers()) Collect(child, result);
        foreach (INamedTypeSymbol type in value.GetTypeMembers()) Collect(type, result);
    }

    private static void Collect(INamedTypeSymbol value, ImmutableArray<INamedTypeSymbol>.Builder result)
    {
        if (HasMarker(value)) result.Add(value);
        foreach (INamedTypeSymbol nested in value.GetTypeMembers()) Collect(nested, result);
    }

    private static bool HasMarker(INamedTypeSymbol type) => type.GetAttributes().Any(attribute =>
        string.Equals(attribute.AttributeClass?.ToDisplayString(), PassParametersAttributeName, StringComparison.Ordinal) ||
        string.Equals(attribute.AttributeClass?.ToDisplayString(), ShaderParametersAttributeName, StringComparison.Ordinal));

    private static bool IsSupportedShape(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.IsRecord || type.IsReadOnly || type.IsGenericType || type.ContainingType is not null)
            return false;
        return type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is StructDeclarationSyntax syntax &&
            syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)));
    }

    private static ParameterFieldKind Classify(ITypeSymbol type)
    {
        string display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.Equals(display, BufferParameterName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (string.Equals(display, BufferParameterArrayName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (string.Equals(display, TextureParameterName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (string.Equals(display, TextureParameterArrayName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (string.Equals(display, SamplerParameterName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (string.Equals(display, SamplerParameterArrayName, StringComparison.Ordinal)) return ParameterFieldKind.Descriptor;
        if (type is INamedTypeSymbol named && named.IsGenericType &&
            string.Equals(named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ConstantParameterName, StringComparison.Ordinal))
            return ParameterFieldKind.Constant;
        return ParameterFieldKind.Unsupported;
    }

    private static string Emit(INamedTypeSymbol type, ImmutableArray<IFieldSymbol> fields)
    {
        StringBuilder source = new();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            source.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
            source.AppendLine();
        }
        source.Append("partial struct ").Append(Escape(type.Name)).AppendLine();
        source.AppendLine("{");
        source.AppendLine("    public readonly global::SomeEngine.RenderGraph.GeneratedParameterSet Pair(");
        source.AppendLine("        ref global::SomeEngine.RenderGraph.GraphBuilder graph,");
        source.AppendLine("        ref global::SomeEngine.RenderGraph.PassBuilder pass,");
        source.AppendLine("        in global::SomeEngine.RenderGraph.ShaderParameterBinding pairing)");
        source.AppendLine("    {");
        source.AppendLine("        global::SomeEngine.RenderGraph.GeneratedParameterDeclaration[] declarations =");
        source.AppendLine("        [");
        foreach (IFieldSymbol field in fields)
        {
            source.Append("            global::SomeEngine.RenderGraph.GeneratedParameterBinding.Describe(in this.")
                .Append(Escape(field.Name)).AppendLine("),");
        }
        source.AppendLine("        ];");
        source.AppendLine("        global::SomeEngine.RenderGraph.GeneratedParameterSet generated =");
        source.AppendLine("            global::SomeEngine.RenderGraph.GeneratedParameterBinding.Pair(ref graph, ref pass, pairing, declarations);");
        foreach (IFieldSymbol field in fields)
        {
            if (Classify(field.Type) != ParameterFieldKind.Constant) continue;
            source.Append("        global::SomeEngine.RenderGraph.GeneratedParameterBinding.Pack(generated, in this.")
                .Append(Escape(field.Name)).AppendLine(");");
        }
        source.AppendLine("        global::SomeEngine.RenderGraph.GeneratedParameterBinding.Seal(generated);");
        source.AppendLine("        return generated;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public readonly void Bind(");
        source.AppendLine("        global::SomeEngine.RenderGraph.GeneratedParameterSet generated,");
        source.AppendLine("        global::SomeEngine.Graphics.ICommandContext commands,");
        source.AppendLine("        in global::SomeEngine.RenderGraph.PassResources resources)");
        source.AppendLine("    {");
        source.AppendLine("        global::SomeEngine.RenderGraph.GeneratedParameterBinding.Bind(generated, commands, resources);");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    private static string Sanitize(string value)
    {
        StringBuilder result = new(value.Length);
        foreach (char character in value)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        return result.ToString();
    }

    private enum ParameterFieldKind : byte
    {
        Unsupported,
        Descriptor,
        Constant,
    }
}
