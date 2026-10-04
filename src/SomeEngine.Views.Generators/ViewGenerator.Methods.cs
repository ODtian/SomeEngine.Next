using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    // Compose and Solid functions share view parameters, equality, and callback forwarding.
    private static void GenerateMethodViews(SourceProductionContext output, Compilation compilation, CSharpParseOptions options,
        List<SyntaxTree> declarations, ImmutableArray<IMethodSymbol> methods)
    {
        foreach (IMethodSymbol method in methods.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
        {
            bool component = ViewCalls.IsSolidComponent(method);
            if (!component && !ViewCalls.IsComposeView(method) && !ViewCalls.IsComposeGroup(method))
                continue;
            INamedTypeSymbol type = method.ContainingType;
            INamedTypeSymbol? enclosing = GetContainingTypes(type).Append(type).FirstOrDefault(value =>
                !value.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                    declaration.Modifiers.Any(SyntaxKind.PartialKeyword)));
            if (enclosing is not null)
            {
                output.ReportDiagnostic(Diagnostic.Create(ViewDiagnostics.PartialContainerRequired, enclosing.Locations[0], enclosing.Name));
                continue;
            }

            if (ViewCalls.IsComposeGroup(method))
            {
                GenerateComposeGroup(output, compilation, options, declarations, method);
                continue;
            }

            string viewName = GetMethodViewName(method);
            bool hasContent = !component && method.Parameters.Any(ViewCalls.IsContentParameter);
            var callbacks = method.Parameters.Where(parameter => !IsContent(parameter) && IsCallback(parameter.Type)).ToArray();
            string access = method.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
            if (method.Parameters.Any(parameter => ContainsPrivateType(parameter.Type)) ||
                method.TypeParameters.Any(parameter => parameter.ConstraintTypes.Any(ContainsPrivateType)))
                access = "private";
            var text = OpenContainingTypes(type).Append(GeneratePartialType(type)).AppendLine("\n{")
                .Append("    ").Append(access).Append(" readonly record struct @").Append(viewName)
                .Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
                .Append(GenerateParameters(method.Parameters)).Append(')')
                .Append(" : global::SomeEngine.Views.IView, global::SomeEngine.ECS.Components.ISparseComponent")
                .AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {");
            if (component)
                text.AppendLine("        public static global::SomeEngine.Views.BuildMode Mode => global::SomeEngine.Views.BuildMode.Once;");
            text.Append("        [global::System.CodeDom.Compiler.GeneratedCode(\"SomeEngine.Views.Generators.")
                .Append(component ? "Solid" : "Compose").AppendLine("\", \"1.0\")]")
                .AppendLine("        public global::SomeEngine.Views.BuildTask Build()\n        {");
            if (callbacks.Length > 0)
                text.Append("            var callbacks = new @").Append(viewName).Append("Callbacks")
                    .Append(GenerateTypeParameters(method.TypeParameters))
                    .AppendLine("(global::SomeEngine.Views.Scope.World, global::SomeEngine.Views.Scope.Entity);");
            text.Append(method.ReturnsVoid ? "            " : "            return ")
                .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(".@").Append(hasContent ? "Build" + viewName + "Content" : method.Name)
                .Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
                .Append(string.Join(", ", method.Parameters.Select(parameter =>
                    !IsContent(parameter) && IsCallback(parameter.Type)
                        ? GenerateCallbackArgument(parameter.Name, parameter.Type) : "this.@" + parameter.Name))).AppendLine(");");
            if (method.ReturnsVoid)
                text.AppendLine("            return default;");
            text.AppendLine("        }");
            GenerateMethodEquality(text, method, viewName);
            text.AppendLine("    }");
            if (callbacks.Length > 0)
                GenerateCallbackFunctions(text, viewName, method, callbacks.Select(parameter => (parameter.Name, parameter.Type)));
            if (hasContent)
            {
                GenerateContentViews(text, method);
                GenerateContentBody(output, compilation, options, declarations, method);
            }
            text.AppendLine("}");
            CloseContainingTypes(text, type);
            AddDeclarations(output, options, declarations, GetFileName(type, viewName), text.ToString());
        }
    }

    private static void GenerateMethodInterceptor(StringBuilder text, IMethodSymbol method,
        InterceptableLocation location, ulong key, int number)
    {
        method = method.OriginalDefinition;
        var typeParameters = GetContainingTypes(method.ContainingType).Append(method.ContainingType)
            .SelectMany(type => type.TypeParameters).Concat(method.TypeParameters).ToImmutableArray();
        text.Append("        [global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
            .Append(location.Version).Append(", ").Append(SymbolDisplay.FormatLiteral(location.Data, true)).AppendLine(")]")
            .Append("        public static ").Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(" ComponentAt").Append(number)
            .Append(GenerateTypeParameters(typeParameters)).Append('(').Append(GenerateParameters(method.Parameters)).Append(')')
            .AppendLine(GenerateConstraints(typeParameters)).AppendLine("        {");
        if (ViewCalls.IsComposeGroup(method))
        {
            text.Append("            ").Append(method.ReturnsVoid ? "" : "return ")
                .Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(".@").Append(GetComposeGroupName(method)).Append(GenerateTypeParameters(method.TypeParameters))
                .Append('(').Append(key.ToString(CultureInfo.InvariantCulture)).Append("UL");
            if (method.Parameters.Length > 0)
                text.Append(", ").Append(GenerateArguments(method.Parameters));
            text.AppendLine(");\n        }");
            return;
        }
        text.Append("            global::SomeEngine.Views.Scope.View(new ")
            .Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(".@").Append(GetMethodViewName(method)).Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
            .Append(GenerateArguments(method.Parameters)).Append("), ")
            .Append(key.ToString(CultureInfo.InvariantCulture)).AppendLine("UL);");
        if (!method.ReturnsVoid)
            text.AppendLine("            return default;");
        text.AppendLine("        }");
    }

    private static void GenerateRememberInterceptor(StringBuilder text, InterceptableLocation location, ulong key, int number)
    {
        text.Append("        [global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
            .Append(location.Version).Append(", ").Append(SymbolDisplay.FormatLiteral(location.Data, true)).AppendLine(")]")
            .Append("        public static T RememberAt").Append(number).AppendLine("<T>(global::System.Func<T> init)")
            .Append("            => global::SomeEngine.Views.Scope.Remember(init, ")
            .Append(key.ToString(CultureInfo.InvariantCulture)).AppendLine("UL);");
    }

    private static void GenerateMethodEquality(StringBuilder text, IMethodSymbol method, string viewName)
        => GenerateParameterEquality(text, "@" + viewName + GenerateTypeParameters(method.TypeParameters),
            method.Parameters.Select(parameter => (parameter.Name, parameter.Type, IsContent(parameter))));

    private static bool UsesReferenceEquality(ITypeSymbol type, bool content) => content && type.IsReferenceType ||
        type.IsReferenceType && type.SpecialType != SpecialType.System_String && !IsRecordParameter(type);

    private static bool IsRecordParameter(ITypeSymbol type) => type is INamedTypeSymbol { IsRecord: true } ||
        type is ITypeParameterSymbol parameter && parameter.ConstraintTypes.Any(IsRecordParameter);

    private static bool IsContent(IParameterSymbol parameter) => parameter.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute");

    private static bool IsCallback(ITypeSymbol type) => type is INamedTypeSymbol named &&
        named.TypeKind == TypeKind.Delegate && named.ContainingNamespace.ToDisplayString() == "System" &&
        named.Name is "Action" or "Func";

    private static string GetMethodViewName(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        string name = method.Name + "_View";
        if (method.Locations.Any(location => location.IsInSource))
        {
            if (method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>()
                .Count(value => ViewCalls.IsComposeView(value) || ViewCalls.IsSolidComponent(value)) == 1)
                return name;
        }
        else if (method.ContainingType.GetTypeMembers(name, method.Arity).Length > 0)
            return name;

        string parameters = method.Arity.ToString(CultureInfo.InvariantCulture) + ":" +
            string.Join(";", method.Parameters.Select(parameter => parameter.RefKind + " " +
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return name + "_" + HashText(parameters).ToString("X16", CultureInfo.InvariantCulture);
    }
}
