using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static void GenerateComposeGroup(SourceProductionContext output, Compilation compilation, CSharpParseOptions options,
        List<SyntaxTree> declarations, IMethodSymbol method)
    {
        string keyParameter = "callKey";
        while (method.Parameters.Any(parameter => parameter.Name == keyParameter))
            keyParameter += "_";
        INamedTypeSymbol type = method.ContainingType;
        string name = GetComposeGroupName(method);
        bool hasContent = method.Parameters.Any(ViewCalls.IsContentParameter);
        string access = method.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var text = OpenContainingTypes(type).Append(GeneratePartialType(type)).AppendLine("\n{")
            .AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"SomeEngine.Views.Generators.Compose\", \"1.0\")]")
            .Append("    ").Append(access).Append(" static ")
            .Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(" @").Append(name).Append(GenerateTypeParameters(method.TypeParameters))
            .Append("(ulong @").Append(keyParameter);
        if (method.Parameters.Length > 0)
            text.Append(", ").Append(GenerateParameters(method.Parameters));
        text.Append(')').AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {")
            .Append("        using (global::SomeEngine.Views.Scope.GroupKeys(@").Append(keyParameter).AppendLine("))")
            .AppendLine("        {")
            .Append("            ").Append(method.ReturnsVoid ? "" : "return ")
            .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(".@")
            .Append(hasContent ? "Build" + GetMethodViewName(method) + "Content" : method.Name)
            .Append(GenerateTypeParameters(method.TypeParameters)).Append('(')
            .Append(GenerateArguments(method.Parameters)).AppendLine(");")
            .AppendLine("        }\n    }");
        if (hasContent)
        {
            GenerateContentViews(text, method);
            GenerateContentBody(output, compilation, options, declarations, method);
        }
        text.AppendLine("}");
        CloseContainingTypes(text, type);
        AddDeclarations(output, options, declarations, GetFileName(type, name), text.ToString());
    }

    private static string GetComposeGroupName(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        string parameters = method.Arity.ToString(CultureInfo.InvariantCulture) + ":" +
            string.Join(";", method.Parameters.Select(parameter => parameter.RefKind + " " +
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return "Run" + method.Name + "Group_" + HashText(parameters).ToString("X16", CultureInfo.InvariantCulture);
    }
}
