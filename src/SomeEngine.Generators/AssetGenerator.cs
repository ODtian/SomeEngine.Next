using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

[Generator]
public sealed class AssetGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidFields = new(
        "SEAS001", "Invalid asset fields", "Asset fields '{0}': {1}", "SomeEngine.Assets",
        DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fields = context.SyntaxProvider.ForAttributeWithMetadataName(
            "SomeEngine.Assets.AssetAttribute", static (node, _) => node is ClassDeclarationSyntax,
            static (value, _) => value);
        context.RegisterSourceOutput(fields, static (output, value) => Emit(output, value));
    }

    private static void Emit(SourceProductionContext output, GeneratorAttributeSyntaxContext context)
    {
        var fields = (INamedTypeSymbol)context.TargetSymbol;
        AttributeData attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol type ||
            attribute.ConstructorArguments[1].Value is not string suffix || !suffix.StartsWith(".", StringComparison.Ordinal))
        {
            Error("requires a domain type and a file suffix starting with '.'");
            return;
        }
        if (SymbolEqualityComparer.Default.Equals(type, fields))
        {
            Error("the domain type and file fields must be different types");
            return;
        }
        if (fields.Arity != 0 || fields.ContainingType is not null || type.TypeKind != TypeKind.Class || type.IsStatic)
        {
            Error("requires non-generic top-level fields and an instance domain class");
            return;
        }
        if (!fields.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "SomeEngine.Serialization.BinaryContractAttribute") &&
            !fields.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "SomeEngine.Serialization.IBinaryContract<TSelf>" || i.Name == "IBinaryContract"))
        {
            Error("requires generated or explicit binary serialization on the file fields");
            return;
        }
        if (!fields.GetMembers("AssetGuid").OfType<IPropertySymbol>().Any(p => p.Type.SpecialType == SpecialType.System_String && p.SetMethod is not null))
        {
            Error("requires an AssetGuid string in the file fields");
            return;
        }
        string data = fields.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string domain = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string registration = "__" + fields.Name + "AssetFormat";
        string writer = fields.GetMembers("CreateWriter").OfType<IMethodSymbol>().Any(m => m.IsStatic)
            ? data + ".CreateWriter" : "global::SomeEngine.Serialization.Containers.BinaryDocumentWriter.Create";
        string dependencies = fields.GetMembers("GetDependencies").Length != 0
            ? "static (fields, path) => fields.GetDependencies(path)" : "static (_, _) => global::System.Array.Empty<global::System.Guid>()";
        string name = fields.GetMembers("Name").Length != 0 ? "fields.Name ?? string.Empty" : "string.Empty";
        string copy;
        if (fields.GetMembers("CopyFields").OfType<IMethodSymbol>().Any(m => !m.IsStatic && m.Parameters.Length == 0))
            copy = "static fields => fields.CopyFields()";
        else
        {
            var body = new StringBuilder("static fields => { var copy = global::SomeEngine.Serialization.BinaryContractSerializer.Copy(fields);");
            foreach (IPropertySymbol property in fields.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.GetMethod is null || property.SetMethod is null ||
                    !property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "SomeEngine.Serialization.BinaryIgnoreAttribute"))
                    continue;
                string propertyType = property.Type.ToDisplayString();
                if (propertyType is "System.Memory<byte>" or "System.ReadOnlyMemory<byte>")
                    body.Append(" copy.").Append(property.Name).Append(" = fields.").Append(property.Name).Append(".ToArray();");
                else if (propertyType is "System.Memory<byte>?" or "System.ReadOnlyMemory<byte>?")
                    body.Append(" copy.").Append(property.Name).Append(" = fields.").Append(property.Name)
                        .Append(".HasValue ? new global::").Append(propertyType.TrimEnd('?')).Append("(fields.")
                        .Append(property.Name).Append(".Value.ToArray()) : default(global::")
                        .Append(propertyType).Append(");");
                else if (propertyType is "byte[]" or "byte[]?")
                    body.Append(" copy.").Append(property.Name).Append(" = fields.").Append(property.Name).Append("?.ToArray();");
            }
            copy = body.Append(" return copy; }").ToString();
        }
        StringBuilder code = new();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        if (!fields.ContainingNamespace.IsGlobalNamespace)
            code.Append("namespace ").Append(fields.ContainingNamespace.ToDisplayString()).AppendLine(";");
        code.Append("internal static class ").Append(registration).AppendLine(" {");
        code.AppendLine("[global::System.Runtime.CompilerServices.ModuleInitializer] internal static void Register() {");
        code.Append("global::SomeEngine.Assets.Asset<").Append(domain).Append(">.RegisterGenerated<").Append(data).AppendLine(">(");
        code.Append("typeof(").Append(domain).Append(").FullName!, ").Append(Literal(suffix)).AppendLine(",");
        code.AppendLine("static fields => global::System.Guid.Parse(fields.AssetGuid!),");
        code.Append("static fields => ").Append(name).AppendLine(",");
        code.Append(dependencies).AppendLine(",");
        code.Append("static fields => ").Append(writer).Append("(fields), ").Append(copy).AppendLine("); }");
        code.AppendLine("}");
        code.Append(fields.DeclaredAccessibility == Accessibility.Public ? "public " : "internal ");
        code.Append("partial class ").Append(fields.Name).AppendLine(" {");
        code.Append("public global::SomeEngine.Assets.Asset<").Append(domain).AppendLine("> ToAsset(global::System.Guid id = default) {");
        code.AppendLine("if (id != global::System.Guid.Empty) AssetGuid = id.ToString();");
        code.AppendLine("if (string.IsNullOrEmpty(AssetGuid)) AssetGuid = global::System.Guid.NewGuid().ToString();");
        code.Append("return global::SomeEngine.Assets.Asset<").Append(domain).AppendLine(">.CreateGenerated(");
        code.Append("this, global::System.Guid.Parse(AssetGuid), ").Append(name.Replace("fields.", "")).AppendLine(",");
        code.Append(fields.GetMembers("GetDependencies").Length != 0 ? "GetDependencies(string.Empty)" : "global::System.Array.Empty<global::System.Guid>()").AppendLine(",");
        code.Append("static fields => ").Append(writer).Append("(fields), ").Append(copy).AppendLine("); }");
        code.AppendLine("}");
        output.AddSource(registration + ".g.cs", code.ToString());
        void Error(string message) => output.ReportDiagnostic(Diagnostic.Create(InvalidFields,
            fields.Locations.FirstOrDefault(), fields.ToDisplayString(), message));
    }

    private static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
