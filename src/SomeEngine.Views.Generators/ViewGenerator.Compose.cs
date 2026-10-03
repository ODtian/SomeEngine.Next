using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static void GenerateComposeViews(SourceProductionContext output, CSharpParseOptions options,
        List<SyntaxTree> declarations, ImmutableArray<IMethodSymbol> methods)
    {
        foreach (IMethodSymbol method in methods)
        {
            if (!ViewCalls.IsComposeView(method))
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

            string viewName = GetComposeViewName(method);
            string access = method.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
            if (method.Parameters.Any(parameter => ContainsPrivateType(parameter.Type)))
                access = "private";
            var text = OpenContainingTypes(type).Append(GeneratePartialType(type)).AppendLine("\n{")
                .Append("    ").Append(access).Append(" readonly record struct @").Append(viewName)
                .Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
                .Append(GenerateParameters(method.Parameters)).Append(')')
                .Append(" : global::SomeEngine.Views.IView, global::SomeEngine.ECS.Components.ISparseComponent")
                .AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {")
                .AppendLine("        [global::System.CodeDom.Compiler.GeneratedCode(\"SomeEngine.Views.Generators.Compose\", \"1.0\")]")
                .Append("        public global::SomeEngine.Views.BuildTask Build()")
                .Append(method.ReturnsVoid ? "\n        {\n            " : " => ")
                .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(".@").Append(method.Name).Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
                .Append(GenerateArguments(method.Parameters)).AppendLine(");");
            if (method.ReturnsVoid)
                text.AppendLine("            return default;\n        }");
            text.AppendLine("    }\n}");
            CloseContainingTypes(text, type);
            AddDeclarations(output, options, declarations, GetFileName(type, viewName), text.ToString());
        }
    }

    private static void GenerateComposeInterceptor(StringBuilder text, IMethodSymbol method,
        InterceptableLocation location, ulong key, int number)
    {
        method = method.OriginalDefinition;
        var typeParameters = GetContainingTypes(method.ContainingType).Append(method.ContainingType)
            .SelectMany(type => type.TypeParameters).Concat(method.TypeParameters).ToImmutableArray();
        text.Append("        [global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
            .Append(location.Version).Append(", ").Append(SymbolDisplay.FormatLiteral(location.Data, true)).AppendLine(")]")
            .Append("        public static ").Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(" ComposeAt").Append(number)
            .Append(GenerateTypeParameters(typeParameters)).Append('(').Append(GenerateParameters(method.Parameters)).Append(')')
            .AppendLine(GenerateConstraints(typeParameters)).AppendLine("        {")
            .Append("            global::SomeEngine.Views.Scope.View(new ")
            .Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(".@").Append(GetComposeViewName(method)).Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
            .Append(GenerateArguments(method.Parameters)).Append("), ")
            .Append(key.ToString(CultureInfo.InvariantCulture)).AppendLine("UL);");
        if (!method.ReturnsVoid)
            text.AppendLine("            return default;");
        text.AppendLine("        }");
    }

    private static string GetComposeViewName(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        string name = method.Name + "_View";
        if (method.Locations.Any(location => location.IsInSource))
        {
            if (method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>().Count(ViewCalls.IsComposeView) == 1)
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
