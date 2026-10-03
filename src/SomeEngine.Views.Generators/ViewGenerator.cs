using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SomeEngine.Views.Generators;

[Generator(LanguageNames.CSharp)]
public sealed partial class ViewGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var views = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is TypeDeclarationSyntax,
            static (syntax, cancellation) =>
            {
                var type = (INamedTypeSymbol?)syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellation);
                return type is { TypeKind: TypeKind.Struct } &&
                    type.AllInterfaces.Any(static value => value.ToDisplayString() == "SomeEngine.Views.IView")
                    ? type : null;
            }).Where(static type => type is not null).Select(static (type, _) => type!);

        var composables = context.SyntaxProvider.ForAttributeWithMetadataName(
            "SomeEngine.Views.Compose.ComposableAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (syntax, _) => (IMethodSymbol)syntax.TargetSymbol);

        context.RegisterSourceOutput(views.Collect().Combine(composables.Collect()).Combine(context.CompilationProvider),
            static (output, input) => Generate(output, input.Right, input.Left.Left, input.Left.Right));
    }

    private static void Generate(SourceProductionContext output, Compilation compilation,
        ImmutableArray<INamedTypeSymbol> views, ImmutableArray<IMethodSymbol> composables)
    {
        var options = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        var functions = new List<SyntaxTree>();
        GenerateComposeViews(output, compilation, options, functions, composables);
        Compilation composeCompilation = compilation.AddSyntaxTrees(functions);
        foreach (SyntaxTree tree in functions)
        {
            SemanticModel model = composeCompilation.GetSemanticModel(tree);
            foreach (TypeDeclarationSyntax declaration in tree.GetRoot(output.CancellationToken)
                .DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (declaration.BaseList is not null &&
                    model.GetDeclaredSymbol(declaration, output.CancellationToken) is INamedTypeSymbol { TypeKind: TypeKind.Struct } type &&
                    type.AllInterfaces.Any(value => value.ToDisplayString() == "SomeEngine.Views.IView"))
                    views = views.Add(type);
            }
        }
        foreach (INamedTypeSymbol type in views.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            INamedTypeSymbol[] containers = GetContainingTypes(type!).ToArray();
            INamedTypeSymbol? enclosing = containers.FirstOrDefault(value => !value.DeclaringSyntaxReferences
                .Any(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)));
            if (enclosing is not null)
            {
                output.ReportDiagnostic(Diagnostic.Create(ViewDiagnostics.PartialContainerRequired, enclosing.Locations[0], enclosing.Name));
                continue;
            }
            if (!type.AllInterfaces.Any(value => value.ToDisplayString() is "SomeEngine.ECS.IComponent" or "SomeEngine.ECS.Components.ISparseComponent"))
            {
                output.ReportDiagnostic(Diagnostic.Create(ViewDiagnostics.ChooseStorage, type.Locations[0], type.Name));
                var declaration = (TypeDeclarationSyntax)type.DeclaringSyntaxReferences[0].GetSyntax(output.CancellationToken);
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    output.ReportDiagnostic(Diagnostic.Create(ViewDiagnostics.PartialViewRequired, type.Locations[0], type.Name));
                    continue;
                }
                var storage = OpenContainingTypes(type);
                storage.Append(GeneratePartialType(type)).AppendLine(" : global::SomeEngine.ECS.Components.ISparseComponent { }");
                CloseContainingTypes(storage, type);
                AddDeclarations(output, options, functions, GetFileName(type, "Storage"), storage.ToString());
            }
            string? stateProperties = GenerateStateProperties(type);
            if (stateProperties is not null)
                AddDeclarations(output, options, functions, GetFileName(type, "State"), stateProperties);
            string source = GenerateFunctions(type);
            string file = GetFileName(type, "Functions");
            AddDeclarations(output, options, functions, file, source);
        }

        // Bind function declarations together with the user's calls in this semantic pass.
        compilation = compilation.AddSyntaxTrees(functions);
        var interceptors = new StringBuilder("// <auto-generated/>\n#nullable enable\n")
            .AppendLine("namespace SomeEngine.Views.Generated\n{\n    internal static class CallSiteFunctions\n    {");
        int count = 0;
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot(output.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(call, output.CancellationToken).Symbol is not IMethodSymbol method)
                    continue;
                bool viewCall = ViewCalls.IsView(method);
                bool namedCall = ViewCalls.IsViewFunction(method);
                bool composeCall = ViewCalls.IsComposeView(method);
                if (!viewCall && !namedCall && !composeCall)
                    continue;
                if (composeCall && model.GetEnclosingSymbol(call.SpanStart, output.CancellationToken) is IMethodSymbol enclosingMethod &&
                    ViewCalls.IsComposeBuild(enclosingMethod))
                    continue;
                if (namedCall && !compilation.IsSymbolAccessibleWithin(method.ContainingType, compilation.Assembly))
                    continue;
                InterceptableLocation? location = model.GetInterceptableLocation(call, output.CancellationToken);
                if (location is null)
                    continue;
                SimpleNameSyntax name = GetMethodName(call);
                string? sourceKey = call.GetAnnotations("ViewCallKey").FirstOrDefault()?.Data;
                ulong key = sourceKey is null ? GetCallKey(name.GetLocation().GetMappedLineSpan()) :
                    ulong.Parse(sourceKey, CultureInfo.InvariantCulture);
                if (composeCall)
                {
                    GenerateComposeInterceptor(interceptors, method, location, key, count++);
                    continue;
                }
                interceptors.Append("        [global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
                    .Append(location.Version).Append(", ")
                    .Append(SymbolDisplay.FormatLiteral(location.Data, true)).AppendLine(")]")
                    .Append("        public static global::SomeEngine.ECS.Entities.Entity ViewAt")
                    .Append(count++);
                if (viewCall)
                {
                    interceptors.AppendLine("<T>(in T view) where T : struct, global::SomeEngine.Views.IView, global::System.IEquatable<T>")
                        .Append("            => global::SomeEngine.Views.Scope.View(in view, ");
                }
                else
                {
                    method = method.OriginalDefinition;
                    var typeParameters = GetContainingTypes(method.ContainingType).SelectMany(value => value.TypeParameters)
                        .Concat(method.TypeParameters).ToImmutableArray();
                    interceptors.Append(GenerateTypeParameters(typeParameters))
                        .Append('(').Append(GenerateParameters(method.Parameters)).Append(')')
                        .AppendLine(GenerateConstraints(typeParameters))
                        .Append("            => ").Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .Append(".Declare").Append(method.Name).Append(GenerateTypeParameters(method.TypeParameters)).Append('(');
                }
                interceptors.Append(key.ToString(CultureInfo.InvariantCulture)).Append("UL");
                if (namedCall && method.Parameters.Length > 0)
                    interceptors.Append(", ").Append(GenerateArguments(method.Parameters));
                interceptors.AppendLine(");");
            }
        }
        if (count == 0)
            return;
        interceptors.AppendLine("    }\n}\nnamespace System.Runtime.CompilerServices\n{")
            .AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]")
            .AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute\n    {\n        public InterceptsLocationAttribute(int version, string data) { }\n    }\n}");
        output.AddSource("ViewInterceptors.g.cs", interceptors.ToString());
    }

    private static void AddDeclarations(SourceProductionContext output, CSharpParseOptions options,
        List<SyntaxTree> declarations, string file, string source)
    {
        var text = SourceText.From(source, Encoding.UTF8);
        output.AddSource(file, text);
        declarations.Add(CSharpSyntaxTree.ParseText(text, options, file, output.CancellationToken));
    }

    private static string GenerateFunctions(INamedTypeSymbol type)
    {
        var constructor = type.InstanceConstructors.FirstOrDefault(value =>
            value.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax));
        var text = OpenContainingTypes(type);
        string access = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        if (constructor?.Parameters.Any(parameter => ContainsPrivateType(parameter.Type)) == true)
            access = "private";
        text.Append(access).Append(" static partial class ").Append(type.Name).AppendLine("Functions\n{");
        GenerateSerialization(text, type);
        if (constructor is null)
        {
            text.AppendLine("}");
            CloseContainingTypes(text, type);
            return text.ToString();
        }
        string keyParameter = "callKey";
        for (int suffix = 1; constructor.Parameters.Any(parameter => parameter.Name == keyParameter); suffix++)
            keyParameter = "callKey" + suffix.ToString(CultureInfo.InvariantCulture);
        text.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"SomeEngine.Views.Generators\", \"1.0\")]")
            .Append("    public static global::SomeEngine.ECS.Entities.Entity @").Append(type.Name)
            .Append(GenerateTypeParameters(type.TypeParameters)).Append('(')
            .Append(GenerateParameters(constructor.Parameters))
            .Append(')').Append(GenerateConstraints(type.TypeParameters))
            .Append(" => global::SomeEngine.Views.Scope.View(new ")
            .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('(')
            .Append(GenerateArguments(constructor.Parameters))
            .AppendLine("));")
            .Append("    public static global::SomeEngine.ECS.Entities.Entity Declare").Append(type.Name)
            .Append(GenerateTypeParameters(type.TypeParameters)).Append("(ulong ").Append(keyParameter);
        if (constructor.Parameters.Length > 0)
            text.Append(", ").Append(GenerateParameters(constructor.Parameters));
        text.Append(')').Append(GenerateConstraints(type.TypeParameters))
            .Append(" => global::SomeEngine.Views.Scope.View(new ")
            .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('(')
            .Append(GenerateArguments(constructor.Parameters)).Append("), ").Append(keyParameter).AppendLine(");\n}");
        CloseContainingTypes(text, type);
        return text.ToString();
    }

    private static void GenerateSerialization(StringBuilder text, INamedTypeSymbol type)
    {
        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string register = type.AllInterfaces.Any(value => value.ToDisplayString() == "SomeEngine.ECS.IComponent")
            ? "Register" : "RegisterSparse";
        bool generic = type.TypeParameters.Length > 0 || GetContainingTypes(type).Any(value => value.TypeParameters.Length > 0);
        string keyParameter = generic ? ", global::SomeEngine.ECS.Serialization.SerializationTypeKey typeKey" : "";
        if (!generic)
            text.Append("    public static global::SomeEngine.ECS.Serialization.SerializationTypeKey SerializationType => ")
                .Append(GenerateSerializationKey(type)).AppendLine(";");
        string key = generic ? "" : "        var typeKey = SerializationType;\n";
        IFieldSymbol[] fields = ViewParameterCodecs.GetFields(type);
        bool generatedCodec = ViewParameterCodecs.CanEncode(fields);
        if (generatedCodec)
        {
            string codec = "ParameterCodec" + GenerateTypeParameters(type.TypeParameters);
            bool references = fields.Any(field => field.Type.ToDisplayString() == "SomeEngine.ECS.Entities.Entity");
            text.Append("    public static void RegisterSerialization").Append(GenerateTypeParameters(type.TypeParameters))
                .Append("(global::SomeEngine.ECS.Serialization.SerializationRegistry registry").Append(keyParameter).Append(')')
                .AppendLine(GenerateConstraints(type.TypeParameters)).AppendLine("    {").Append(key)
                .Append("        registry.").Append(register).Append('<').Append(typeName).Append(", ").Append(codec);
            if (references)
                text.Append(", ").Append(codec);
            text.AppendLine(">(typeKey);")
                .Append("        global::SomeEngine.Views.ViewSerialization.RegisterType<").Append(typeName).AppendLine(">(typeKey);\n    }");
        }
        string codecParameter = "TViewCodec";
        while (type.TypeParameters.Concat(GetContainingTypes(type).SelectMany(value => value.TypeParameters))
            .Any(value => value.Name == codecParameter))
            codecParameter += "Value";
        string codecArguments = string.Join(", ", type.TypeParameters.Select(value => "@" + value.Name).Concat([codecParameter]));
        string codecAccess = ContainsPrivateType(type) ? "private" : "public";
        string codecKeyParameter = generic ? keyParameter : ", global::SomeEngine.ECS.Serialization.SerializationTypeKey? serializationType = null";
        string codecKey = generic ? "" : "        var typeKey = serializationType ?? SerializationType;\n";
        text.Append("    ").Append(codecAccess).Append(" static void RegisterSerialization<").Append(codecArguments)
            .Append(">(global::SomeEngine.ECS.Serialization.SerializationRegistry registry").Append(codecKeyParameter).Append(')')
            .AppendLine(GenerateConstraints(type.TypeParameters))
            .Append("        where ").Append(codecParameter).Append(" : struct, global::SomeEngine.ECS.Serialization.IComponentCodec<")
            .Append(typeName).AppendLine(">\n    {").Append(codecKey)
            .Append("        registry.").Append(register).Append('<').Append(typeName).Append(", ").Append(codecParameter).AppendLine(">(typeKey);")
            .Append("        global::SomeEngine.Views.ViewSerialization.RegisterType<").Append(typeName).AppendLine(">(typeKey);\n    }");
        if (generatedCodec)
            ViewParameterCodecs.Generate(text, type, fields, GenerateTypeParameters(type.TypeParameters), GenerateConstraints(type.TypeParameters));
    }

    private static string GenerateSerializationKey(INamedTypeSymbol type)
    {
        string name = type.ContainingAssembly.Name + ":" + type.ToDisplayString();
        using var hash = SHA256.Create();
        string id = new Guid(hash.ComputeHash(Encoding.UTF8.GetBytes(name)).Take(16).ToArray()).ToString();
        string fields = string.Join(";", ViewParameterCodecs.GetFields(type)
            .Select(field => field.MetadataName + ":" + GetSerializedTypeName(field.Type)));
        string storage = type.AllInterfaces.Any(value => value.ToDisplayString() == "SomeEngine.ECS.IComponent") ? "table" : "sparse";
        ulong schema = HashText(name + ":fields-v1:" + storage + ":" + fields);
        return "new global::SomeEngine.ECS.Serialization.SerializationTypeKey(new global::System.Guid(\"" + id + "\"), " +
            SymbolDisplay.FormatLiteral(name, true) + ", " + schema.ToString(CultureInfo.InvariantCulture) + "UL)";
    }

    private static string GetSerializedTypeName(ITypeSymbol type)
    {
        string name = type.ToDisplayString();
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
            return name;
        string values = string.Join(",", enumeration.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue).OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => field.Name + "=" + Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture)));
        return name + ":" + enumeration.EnumUnderlyingType!.ToDisplayString() + ":" + values;
    }

    private static string GenerateArguments(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(parameter =>
            (parameter.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" }) + "@" + parameter.Name));

    private static IEnumerable<INamedTypeSymbol> GetContainingTypes(INamedTypeSymbol type)
    {
        var containers = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? parent = type.ContainingType; parent is not null; parent = parent.ContainingType)
            containers.Push(parent);
        return containers;
    }

    private static StringBuilder OpenContainingTypes(INamedTypeSymbol type)
    {
        var text = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!type.ContainingNamespace.IsGlobalNamespace)
            text.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
        foreach (INamedTypeSymbol container in GetContainingTypes(type))
            text.Append(GeneratePartialType(container)).AppendLine("\n{");
        return text;
    }

    private static void CloseContainingTypes(StringBuilder text, INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol _ in GetContainingTypes(type))
            text.AppendLine("}");
    }

    private static string GeneratePartialType(INamedTypeSymbol type)
    {
        var declaration = (TypeDeclarationSyntax)type.DeclaringSyntaxReferences[0].GetSyntax();
        string modifiers = string.Join(" ", declaration.Modifiers.Where(modifier => !modifier.IsKind(SyntaxKind.PartialKeyword)));
        string kind = (type.IsRecord ? "record " : "") + (type.TypeKind == TypeKind.Struct ? "struct" : "class");
        return modifiers + " partial " + kind + " @" + type.Name + GenerateTypeParameters(type.TypeParameters);
    }

    private static bool ContainsPrivateType(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => ContainsPrivateType(array.ElementType),
        INamedTypeSymbol named => named.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal ||
            named.TypeArguments.Any(ContainsPrivateType) || (named.ContainingType is not null && ContainsPrivateType(named.ContainingType)),
        _ => false,
    };

    private static string GetFileName(INamedTypeSymbol type, string suffix) =>
        type.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_') + "." + suffix + ".g.cs";

    private static string GenerateParameters(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(parameter =>
            parameter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                    SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue)
                .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))));

    private static string GenerateTypeParameters(ImmutableArray<ITypeParameterSymbol> parameters) =>
        parameters.Length == 0 ? "" : "<" + string.Join(", ", parameters.Select(parameter => "@" + parameter.Name)) + ">";

    private static string GenerateConstraints(ImmutableArray<ITypeParameterSymbol> parameters)
    {
        var result = new StringBuilder();
        foreach (ITypeParameterSymbol parameter in parameters)
        {
            var constraints = new List<string>();
            if (parameter.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint)
                constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint)
                constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
            else if (parameter.HasNotNullConstraint)
                constraints.Add("notnull");
            constraints.AddRange(parameter.ConstraintTypes.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            if (parameter.HasConstructorConstraint)
                constraints.Add("new()");
            if (constraints.Count > 0)
                result.Append(" where @").Append(parameter.Name).Append(" : ").Append(string.Join(", ", constraints));
        }
        return result.ToString();
    }

    private static SimpleNameSyntax GetMethodName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name,
        MemberAccessExpressionSyntax member => member.Name,
        MemberBindingExpressionSyntax member => member.Name,
        _ => throw new InvalidOperationException("A view call has a method name."),
    };

    private static ulong GetCallKey(FileLinePositionSpan location)
    {
        string text = location.Path.Replace('\\', '/') + ":" + (location.StartLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture) +
            ":" + (location.StartLinePosition.Character + 1).ToString(CultureInfo.InvariantCulture);
        return HashText(text);
    }

    private static ulong HashText(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in Encoding.UTF8.GetBytes(text))
            hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash == 0 ? 1UL : hash;
    }
}

