using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.ECS.SourceGen;

[Generator]
public sealed class BundleGenerator : IIncrementalGenerator
{
    private const string EcsNamespace = "SomeEngine.ECS";
    private const string ComponentsNamespace = "SomeEngine.ECS.Components";

    private static readonly DiagnosticDescriptor InvalidFieldDiagnostic = new(
        id: "SECSSG001",
        title: "Bundle field must be a supported component type",
        messageFormat: "Field '{0}' in bundle '{1}' must be a component, tag, sparse component, dynamic buffer init, shared component init, or nested bundle",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RelationshipFieldDiagnostic = new(
        id: "SECSSG002",
        title: "Relationship topology cannot be authored in bundles",
        messageFormat: "Field '{0}' in bundle '{1}' uses protected relationship component '{2}'; use its typed relationship API",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateFieldDiagnostic = new(
        id: "SECSSG003",
        title: "Bundle cannot contain duplicate component types",
        messageFormat: "Field '{0}' in bundle '{1}' expands to component type '{2}', which already appears earlier in the bundle",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RecursiveBundleDiagnostic = new(
        id: "SECSSG004",
        title: "Bundle recursion is not supported",
        messageFormat: "Field '{0}' in bundle '{1}' creates a recursive bundle graph involving '{2}'",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InaccessibleFieldDiagnostic = new(
        id: "SECSSG005",
        title: "Bundle field must be accessible to generated code",
        messageFormat: "Field '{0}' in bundle '{1}' must not be private",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BufferElementRule = new(
        id: "SECSSG006",
        title: "Bundle buffer fields must be zero-copy memory",
        messageFormat: "Field '{0}' in bundle '{1}' uses buffer element type '{2}' directly; use ReadOnlyMemory<{2}>",
        category: "SomeEngine.ECS.SourceGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> bundleTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax,
                static (syntaxContext, _) => GetBundleSymbol(syntaxContext))
            .Where(static symbol => symbol is not null)
            .Select(static (symbol, _) => symbol!)
            .Collect();

        context.RegisterSourceOutput(bundleTypes, static (sourceContext, bundles) => Execute(sourceContext, bundles));
    }

    private static INamedTypeSymbol? GetBundleSymbol(GeneratorSyntaxContext context)
    {
        var syntax = (StructDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax) is not INamedTypeSymbol symbol)
            return null;

        return ImplementsInterface(symbol, ComponentsNamespace, "IComponentBundle") ? symbol : null;
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<INamedTypeSymbol> bundles)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var bundle in bundles)
        {
            if (!seen.Add(bundle))
                continue;

            BundleModel? model = BuildModel(bundle, context);
            if (model is null)
                continue;

            if (!EcsGenerator.Partial(bundle))
            {
                EcsGenerator.Error(context, bundle, "Declare component bundles as partial structs.");
                continue;
            }

            context.AddSource(GetHintName(bundle), GenerateSource(bundle, model));
        }
    }

    private static BundleModel? BuildModel(INamedTypeSymbol bundle, SourceProductionContext context)
    {
        var members = new List<BundleMember>();
        var seenComponentTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<INamedTypeSymbol>();

        if (!ExpandBundle(bundle, bundle, "bundle", members, seenComponentTypes, stack, context))
            return null;

        return new BundleModel(
            bundle.ContainingNamespace.IsGlobalNamespace ? string.Empty : bundle.ContainingNamespace.ToDisplayString(),
            bundle.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            members);
    }

    private static bool ExpandBundle(
        INamedTypeSymbol rootBundle,
        INamedTypeSymbol currentBundle,
        string accessPath,
        List<BundleMember> members,
        HashSet<ITypeSymbol> seenComponentTypes,
        Stack<INamedTypeSymbol> stack,
        SourceProductionContext context)
    {
        stack.Push(currentBundle);
        bool ok = true;

        IOrderedEnumerable<IFieldSymbol> fields = currentBundle.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => !field.IsStatic)
            .OrderBy(field => field.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue);

        foreach (var field in fields)
        {
            if (field.DeclaredAccessibility == Accessibility.Private)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InaccessibleFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString()));
                ok = false;
                continue;
            }

            if (field.Type is not INamedTypeSymbol fieldType)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString()));
                ok = false;
                continue;
            }

            if (ImplementsInterface(fieldType, ComponentsNamespace, "IComponentBundle"))
            {
                if (stack.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, fieldType)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RecursiveBundleDiagnostic,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        rootBundle.ToDisplayString(),
                        fieldType.ToDisplayString()));
                    ok = false;
                    continue;
                }

                if (!ExpandBundle(rootBundle, fieldType, $"{accessPath}.{field.Name}", members, seenComponentTypes, stack, context))
                    ok = false;

                continue;
            }

            ITypeSymbol? bufferElementType = TryBufferElement(fieldType);
            if (bufferElementType is not null)
            {
                if (!seenComponentTypes.Add(bufferElementType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateFieldDiagnostic,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        rootBundle.ToDisplayString(),
                        fieldType.ToDisplayString()));
                    ok = false;
                    continue;
                }

                members.Add(new BundleMember(
                    fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    $"{accessPath}.{field.Name}",
                    BundleMemberKind.Buffer,
                    bufferElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                continue;
            }

            if (ImplementsInterface(fieldType, ComponentsNamespace, "ISharedComponent"))
            {
                if (!seenComponentTypes.Add(fieldType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateFieldDiagnostic,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        rootBundle.ToDisplayString(),
                        fieldType.ToDisplayString()));
                    ok = false;
                    continue;
                }

                members.Add(new BundleMember(
                    fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    $"{accessPath}.{field.Name}",
                    BundleMemberKind.Shared,
                    bufferElementTypeName: null));
                continue;
            }

            if (ImplementsInterface(fieldType, ComponentsNamespace, "IRelationshipSource") ||
                ImplementsInterface(fieldType, ComponentsNamespace, "IRelationshipTarget") ||
                fieldType.AllInterfaces.Any(value => value.Name is "IControlledComponent" or "IComputedComponent"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RelationshipFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString(),
                    fieldType.ToDisplayString()));
                ok = false;
                continue;
            }

            if (ImplementsInterface(fieldType, ComponentsNamespace, "IBufferElement"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    BufferElementRule,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString(),
                    fieldType.ToDisplayString()));
                ok = false;
                continue;
            }

            BundleMemberKind? memberKind = GetMemberKind(fieldType);
            if (memberKind is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString()));
                ok = false;
                continue;
            }

            if (!seenComponentTypes.Add(fieldType))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateFieldDiagnostic,
                    field.Locations.FirstOrDefault(),
                    field.Name,
                    rootBundle.ToDisplayString(),
                    fieldType.ToDisplayString()));
                ok = false;
                continue;
            }

            members.Add(new BundleMember(
                fieldType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                $"{accessPath}.{field.Name}",
                memberKind.Value,
                bufferElementTypeName: null));
        }

        stack.Pop();
        return ok;
    }

    private static BundleMemberKind? GetMemberKind(INamedTypeSymbol type)
    {
        if (ImplementsInterface(type, ComponentsNamespace, "ISparseComponent"))
            return BundleMemberKind.Sparse;

        if (ImplementsInterface(type, ComponentsNamespace, "ITag"))
            return BundleMemberKind.Tag;

        if (ImplementsInterface(type, EcsNamespace, "IComponent"))
            return BundleMemberKind.Table;

        return null;
    }

    private static ITypeSymbol? TryBufferElement(INamedTypeSymbol type)
    {
        if (!type.IsGenericType ||
            type.Name != "ReadOnlyMemory" ||
            type.ContainingNamespace.ToDisplayString() != "System" ||
            type.TypeArguments.Length != 1)
        {
            return null;
        }

        return type.TypeArguments[0] is INamedTypeSymbol { IsValueType: true } elementType ? elementType : null;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string @namespace, string name)
    {
        foreach (var iface in type.AllInterfaces)
        {
            INamedTypeSymbol candidate = iface.OriginalDefinition;
            if (candidate.Name == name && candidate.ContainingNamespace.ToDisplayString() == @namespace)
                return true;
        }

        return false;
    }

    private static string GenerateSource(INamedTypeSymbol bundle, BundleModel model)
    {
        string[] tableAndTagIds = model.Members
            .Where(member => member.Kind != BundleMemberKind.Sparse)
            .SelectMany(ComponentIds)
            .ToArray();
        string[] sparseComponentIds = model.Members
            .Where(member => member.Kind == BundleMemberKind.Sparse)
            .SelectMany(ComponentIds)
            .ToArray();
        string[] writeLines = model.Members
            .Where(member => member.Kind != BundleMemberKind.Tag)
            .OrderBy(member => member.Kind == BundleMemberKind.Shared ? 0 : 1)
            .Select(GetWriteLine)
            .ToArray();
        string self = model.BundleTypeName;
        string contract = "global::SomeEngine.ECS.Components.IComponentBundle<" + self + ">";
        var body = new StringBuilder();
        body.Append("private static readonly int[] __ecsComponentIds = new int[] { ").Append(string.Join(", ", tableAndTagIds)).Append(" };\n");
        body.Append("private static readonly int[] __ecsSparseComponentIds = new int[] { ").Append(string.Join(", ", sparseComponentIds)).Append(" };\n");
        body.Append("static global::System.ReadOnlySpan<int> ").Append(contract).Append(".ComponentIds => __ecsComponentIds;\n");
        body.Append("static global::System.ReadOnlySpan<int> ").Append(contract).Append(".SparseComponentIds => __ecsSparseComponentIds;\n");
        body.Append("static void ").Append(contract).Append(".Write(global::SomeEngine.ECS.BundleWriteView view, in ").Append(self).Append(" state)\n{\n");
        foreach (string line in writeLines) body.Append(line).Append('\n');
        body.Append("}");
        return EcsGenerator.Wrap(bundle, contract, body.ToString());
    }

    private static string Indent(string text, int level)
    {
        var indent = new string(' ', level * 4);
        string[] lines = text.Split('\n');
        return string.Join("\n", lines.Select(line => line.Length == 0 ? line : indent + line));
    }

    private static IEnumerable<string> ComponentIds(BundleMember member)
    {
        if (member.Kind == BundleMemberKind.Buffer)
        {
            yield return $"global::SomeEngine.ECS.ComponentId<global::SomeEngine.ECS.Buffer<{member.ElementTypeName}>>.Value";
            yield break;
        }

        yield return $"global::SomeEngine.ECS.ComponentId<{member.TypeName}>.Value";
    }

    private static string GetWriteLine(BundleMember member)
    {
        return member.Kind switch
        {
            BundleMemberKind.Buffer => $"view.WriteBuffer(in {StateAccess(member)});",
            _ => $"view.Write(in {StateAccess(member)});",
        };
    }

    private static string StateAccess(BundleMember member) =>
        "state" + member.AccessPath.Substring("bundle".Length);

    private static string GetHintName(INamedTypeSymbol bundle)
    {
        var builder = new StringBuilder(bundle.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        for (int i = 0; i < builder.Length; i++)
        {
            if (!char.IsLetterOrDigit(builder[i]))
                builder[i] = '_';
        }

        builder.Append(".Bundle.g.cs");
        return builder.ToString();
    }

    private sealed class BundleModel
    {
        public BundleModel(string @namespace, string bundleTypeName, List<BundleMember> members)
        {
            Namespace = @namespace;
            BundleTypeName = bundleTypeName;
            Members = members;
        }

        public string Namespace { get; }

        public string BundleTypeName { get; }

        public List<BundleMember> Members { get; }
    }

    private sealed class BundleMember
    {
        public BundleMember(
            string typeName,
            string accessPath,
            BundleMemberKind kind,
            string? bufferElementTypeName)
        {
            TypeName = typeName;
            AccessPath = accessPath;
            Kind = kind;
            ElementTypeName = bufferElementTypeName;
        }

        public string TypeName { get; }

        public string AccessPath { get; }

        public BundleMemberKind Kind { get; }

        public string? ElementTypeName { get; }
    }

    private enum BundleMemberKind : byte
    {
        Table,
        Tag,
        Sparse,
        Buffer,
        Shared,
    }
}

