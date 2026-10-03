using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static string? GenerateStateProperties(INamedTypeSymbol type)
    {
        var properties = type.GetMembers().OfType<IPropertySymbol>()
            .Where(property => StatePropertyDeclarations.HasStateAttribute(property) && StatePropertyDeclarations.CanImplement(property))
            .OrderBy(property => property.MetadataName, StringComparer.Ordinal).ToArray();
        if (properties.Length == 0)
            return null;

        var text = OpenContainingTypes(type);
        text.Append(GeneratePartialType(type)).AppendLine("\n{");
        string viewType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        for (int index = 0; index < properties.Length; index++)
        {
            IPropertySymbol property = properties[index];
            var declaration = (PropertyDeclarationSyntax)property.DeclaringSyntaxReferences[0].GetSyntax();
            string valueType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
            string getState = "global::SomeEngine.Views.SwiftUI.StateProperties.Get<" + viewType + ", " + valueType + ">(" +
                index.ToString(CultureInfo.InvariantCulture);
            string state = getState + (declaration.Initializer is null ? ")" : ", field)");
            text.Append("    ").Append(string.Join(" ", declaration.Modifiers)).Append(' ')
                .Append(valueType).Append(" @").Append(property.Name).AppendLine("\n    {");
            foreach (AccessorDeclarationSyntax accessor in declaration.AccessorList!.Accessors)
            {
                text.Append("        ").Append(accessor.Modifiers.ToFullString()).Append(accessor.Keyword.Text)
                    .Append(" => ").Append(state).Append(".Value")
                    .Append(accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ? ";" : " = value;").AppendLine();
            }
            text.AppendLine("    }");
            string access = string.Join(" ", declaration.Modifiers.Where(modifier =>
                modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.InternalKeyword) ||
                modifier.IsKind(SyntaxKind.PrivateKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword)));
            text.Append("    ").Append(access).Append(" global::SomeEngine.Views.SwiftUI.Binding<").Append(valueType)
                .Append("> @").Append(property.Name).Append("Binding");
            if (declaration.Initializer is null)
            {
                text.Append(" => new(").Append(state).AppendLine(");");
            }
            else
            {
                text.AppendLine("\n    {\n        get\n        {\n            var view = this;")
                    .Append("            return new(").Append(getState).Append(", Get").Append(property.Name)
                    .AppendLine("Initial(ref view)));\n        }\n    }")
                    .Append("    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = ")
                    .Append(SymbolDisplay.FormatLiteral("<" + property.Name + ">k__BackingField", true)).AppendLine(")]")
                    .Append("    private static extern ref ").Append(valueType).Append(" Get").Append(property.Name)
                    .Append("Initial(ref ").Append(viewType).AppendLine(" view);");
            }
        }
        text.AppendLine("}");
        CloseContainingTypes(text, type);
        return text.ToString();
    }
}
