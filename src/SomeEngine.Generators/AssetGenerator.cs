using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Generators;

#pragma warning disable RS2008

/// <summary>
/// Registers one closed generic descriptor for each concrete asset value. Generated calls target
/// the concrete type directly; asset values never flow through an interface, object registry, or
/// reflection-based loader.
/// </summary>
[Generator]
public sealed class AssetGenerator : IIncrementalGenerator
{
    private const string AssetAttributeName = "SomeEngine.Assets.AssetAttribute";
    private const string BinaryContractAttributeName = "SomeEngine.Serialization.BinaryContractAttribute";

    private static readonly SymbolDisplayFormat QualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly DiagnosticDescriptor InvalidAsset = new(
        "SEAS001",
        "Asset shape cannot be generated",
        "Asset '{0}' cannot be generated: {1}",
        "SomeEngine.Assets",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<AssetCandidate> candidates =
            context.SyntaxProvider.ForAttributeWithMetadataName(
                AssetAttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (attributeContext, _) => CreateCandidate(attributeContext));

        context.RegisterSourceOutput(
            candidates.Collect(),
            static (productionContext, discovered) => Generate(productionContext, discovered));
    }

    private static AssetCandidate CreateCandidate(GeneratorAttributeSyntaxContext context)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        string pathSuffix = string.Empty;
        AttributeData attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is string configured)
        {
            pathSuffix = configured;
        }

        return new AssetCandidate(type, pathSuffix);
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<AssetCandidate> discovered)
    {
        foreach (AssetCandidate candidate in discovered
                     .GroupBy(static item => item.Type, SymbolEqualityComparer.Default)
                     .Select(static group => group.First())
                     .OrderBy(static item => item.Type.ToDisplayString(), StringComparer.Ordinal))
        {
            AssetModel? model = Validate(context, candidate);
            if (model is null)
                continue;

            string hintName = Sanitize(candidate.Type.ToDisplayString()) + ".Asset.g.cs";
            context.AddSource(hintName, Emit(model.Value));
        }
    }

    private static AssetModel? Validate(
        SourceProductionContext context,
        AssetCandidate candidate)
    {
        INamedTypeSymbol type = candidate.Type;
        List<string> reasons = new();
        if (type.TypeKind != TypeKind.Class)
            reasons.Add("only concrete classes are supported");
        if (type.IsAbstract || type.IsStatic)
            reasons.Add("the asset must be a non-abstract instance class");
        if (type.Arity != 0)
            reasons.Add("generic asset declarations are not supported");
        if (!IsAssemblyAccessible(type))
            reasons.Add("the asset and its containing types must be assembly-accessible");

        IPropertySymbol? assetGuid = null;
        IPropertySymbol? name = null;
        bool customWriter = false;
        bool customLoader = false;
        bool customDependencies = false;
        if (!candidate.PathSuffix.StartsWith(".", StringComparison.Ordinal))
            reasons.Add("the required file suffix must start with '.'");
        if (!HasAttribute(type, BinaryContractAttributeName) && !ImplementsBinaryContract(type))
            reasons.Add("an asset must declare [BinaryContract] or directly implement IBinaryContract<T>");

        assetGuid = FindStringProperty(type, "AssetGuid", requireSetter: true);
        if (assetGuid is null)
            reasons.Add("an asset must expose an assembly-accessible string AssetGuid property with get and set accessors");
        name = FindStringProperty(type, "Name", requireSetter: false);
        customWriter = HasCreateWriter(type);
        customLoader = HasCustomLoader(type);
        customDependencies = HasDependencies(type);

        if (reasons.Count != 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidAsset,
                SourceLocation(type),
                type.ToDisplayString(),
                string.Join("; ", reasons)));
            return null;
        }

        return new AssetModel(
            type,
            candidate.PathSuffix,
            assetGuid,
            name,
            customWriter,
            customLoader,
            customDependencies);
    }

    private static string Emit(AssetModel model)
    {
        string asset = model.Type.ToDisplayString(QualifiedTypeFormat);
        string namespaceName = model.Type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : model.Type.ContainingNamespace.ToDisplayString();
        string registration = "__" + Sanitize(model.Type.ToDisplayString()) + "AssetRegistration";

        StringBuilder source = new();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        if (namespaceName.Length != 0)
        {
            source.Append("namespace ").Append(namespaceName).AppendLine(";");
            source.AppendLine();
        }

        source.Append("internal static class ").Append(registration).AppendLine();
        source.AppendLine("{");
        source.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("    internal static void Register()");
        source.AppendLine("    {");
        source.Append("        global::SomeEngine.Assets.AssetType<").Append(asset)
            .AppendLine(">.RegisterGenerated(");
        source.Append("            new global::SomeEngine.Assets.AssetTypeDescriptor<").Append(asset)
            .AppendLine(">(");
        source.Append("                typeof(").Append(asset).AppendLine(").FullName!,");
        source.Append("                ").Append(Literal(model.PathSuffix)).AppendLine(",");

        source.AppendLine("                new global::SomeEngine.Serialization.Containers.BinaryWireTypeDescriptor(");
        source.Append("                    ").Append(asset).AppendLine(".TypeId,");
        source.Append("                    ").Append(asset).AppendLine(".SchemaFingerprint,");
        source.Append("                    ").Append(asset).AppendLine(".Compatibility,");
        source.Append("                    ").Append(asset).AppendLine(".SchemaEpoch),");
        source.AppendLine("                static value => ParseGuid(value.AssetGuid),");
        source.AppendLine("                static (value, guid) => value.AssetGuid = guid.ToFlatString(),");
        if (model.Name is not null)
            source.AppendLine("                static value => value.Name ?? string.Empty,");
        else
            source.Append("                static _ => nameof(").Append(asset).AppendLine("),");
        if (model.CustomDependencies)
            source.AppendLine("                static (value, path) => value.GetDependencies(path),");
        else
            source.AppendLine("                static (_, _) => global::System.Array.Empty<global::SomeEngine.Assets.AssetGuid>(),");
        if (model.CustomWriter)
            source.Append("                static value => ").Append(asset).AppendLine(".CreateWriter(value),");
        else
            source.AppendLine("                static value => global::SomeEngine.Serialization.Containers.BinaryDocumentWriter.Create(value),");
        source.AppendLine("                LoadAsync));");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    private static async global::System.Threading.Tasks.ValueTask<").Append(asset)
            .AppendLine("> LoadAsync(");
        source.AppendLine("        global::SomeEngine.Assets.AssetLoadContext context,");
        source.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        source.AppendLine("    {");
        if (model.CustomLoader)
        {
            source.Append("        ").Append(asset).Append(" value = await ").Append(asset)
                .AppendLine(".LoadAssetAsync(context, cancellationToken).ConfigureAwait(false);");
        }
        else
        {
            source.Append("        global::SomeEngine.Serialization.Containers.BinaryDocument<")
                .Append(asset).AppendLine("> document = await context");
            source.Append("            .OpenAsync<").Append(asset).AppendLine(">()");
            source.AppendLine("            .ConfigureAwait(false);");
            source.Append("        ").Append(asset).AppendLine(" value = document.Root;");
        }
        source.AppendLine("        global::SomeEngine.Assets.AssetGuid guid = ParseGuid(value.AssetGuid);");
        source.AppendLine("        if (guid != context.AssetGuid)");
        source.AppendLine("        {");
        source.AppendLine("            throw new global::System.IO.InvalidDataException(");
        source.AppendLine("                $\"Asset {context.AssetGuid} root declares GUID {guid}.\");");
        source.AppendLine("        }");
        source.AppendLine("        return value;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    private static global::SomeEngine.Assets.AssetGuid ParseGuid(string? value)");
        source.AppendLine("        => global::SomeEngine.Assets.AssetGuid.TryParse(value, out global::SomeEngine.Assets.AssetGuid guid)");
        source.AppendLine("            ? guid");
        source.AppendLine("            : global::SomeEngine.Assets.AssetGuid.Empty;");
        source.AppendLine("}");
        return source.ToString();
    }

    private static IPropertySymbol? FindStringProperty(
        INamedTypeSymbol type,
        string name,
        bool requireSetter)
        => type.GetMembers(name)
            .OfType<IPropertySymbol>()
            .FirstOrDefault(property =>
                property.Type.SpecialType == SpecialType.System_String
                && property.GetMethod is not null
                && IsAssemblyAccessible(property.GetMethod)
                && (!requireSetter
                    || (property.SetMethod is not null && IsAssemblyAccessible(property.SetMethod))));

    private static bool HasCreateWriter(INamedTypeSymbol type)
        => type.GetMembers("CreateWriter")
            .OfType<IMethodSymbol>()
            .Any(method => method.IsStatic
                && IsAssemblyAccessible(method)
                && method.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type));

    private static bool ImplementsBinaryContract(INamedTypeSymbol type)
        => type.AllInterfaces.Any(candidate =>
            candidate.OriginalDefinition.MetadataName == "IBinaryContract`1"
            && candidate.OriginalDefinition.ContainingNamespace.ToDisplayString() ==
                "SomeEngine.Serialization"
            && candidate.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type));

    private static bool HasCustomLoader(INamedTypeSymbol type)
        => type.GetMembers("LoadAssetAsync")
            .OfType<IMethodSymbol>()
            .Any(method => method.IsStatic
                && IsAssemblyAccessible(method)
                && method.Parameters.Length == 2
                && method.Parameters[0].Type.ToDisplayString() == "SomeEngine.Assets.AssetLoadContext"
                && method.Parameters[1].Type.ToDisplayString() == "System.Threading.CancellationToken");

    private static bool HasDependencies(INamedTypeSymbol type)
        => type.GetMembers("GetDependencies")
            .OfType<IMethodSymbol>()
            .Any(method => !method.IsStatic
                && IsAssemblyAccessible(method)
                && method.Parameters.Length == 1
                && method.Parameters[0].Type.SpecialType == SpecialType.System_String);

    private static bool HasAttribute(ISymbol symbol, string metadataName)
        => symbol.GetAttributes().Any(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private static bool IsAssemblyAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (!IsAssemblyAccessible(current.DeclaredAccessibility))
                return false;
        }
        return true;
    }

    private static bool IsAssemblyAccessible(ISymbol symbol)
        => IsAssemblyAccessible(symbol.DeclaredAccessibility);

    private static bool IsAssemblyAccessible(Accessibility accessibility)
        => accessibility != Accessibility.Private
            && accessibility != Accessibility.Protected
            && accessibility != Accessibility.ProtectedAndInternal;

    private static Location SourceLocation(INamedTypeSymbol type)
        => type.Locations.FirstOrDefault(static location => location.IsInSource)
           ?? Location.None;

    private static string Literal(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Sanitize(string value)
    {
        StringBuilder result = new(value.Length);
        foreach (char character in value)
            result.Append(char.IsLetterOrDigit(character) ? character : '_');
        return result.ToString();
    }

    private readonly struct AssetCandidate
    {
        internal AssetCandidate(INamedTypeSymbol type, string pathSuffix)
        {
            Type = type;
            PathSuffix = pathSuffix;
        }

        internal INamedTypeSymbol Type { get; }
        internal string PathSuffix { get; }
    }

    private readonly struct AssetModel
    {
        internal AssetModel(
            INamedTypeSymbol type,
            string pathSuffix,
            IPropertySymbol? assetGuid,
            IPropertySymbol? name,
            bool customWriter,
            bool customLoader,
            bool customDependencies)
        {
            Type = type;
            PathSuffix = pathSuffix;
            AssetGuid = assetGuid;
            Name = name;
            CustomWriter = customWriter;
            CustomLoader = customLoader;
            CustomDependencies = customDependencies;
        }

        internal INamedTypeSymbol Type { get; }
        internal string PathSuffix { get; }
        internal IPropertySymbol? AssetGuid { get; }
        internal IPropertySymbol? Name { get; }
        internal bool CustomWriter { get; }
        internal bool CustomLoader { get; }
        internal bool CustomDependencies { get; }
    }
}

#pragma warning restore RS2008
