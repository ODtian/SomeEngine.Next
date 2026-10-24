using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static void GenerateAsyncInterceptor(StringBuilder text, IMethodSymbol method,
        InterceptableLocation location, ulong key, int number)
    {
        method = method.OriginalDefinition;
        text.Append("        [global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
            .Append(location.Version).Append(", ").Append(SymbolDisplay.FormatLiteral(location.Data, true)).AppendLine(")]")
            .Append("        public static ")
            .Append(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(' ').Append(method.Name).Append("At").Append(number)
            .Append(GenerateTypeParameters(method.TypeParameters)).Append('(').Append(GenerateParameters(method.Parameters)).Append(')')
            .AppendLine(GenerateConstraints(method.TypeParameters))
            .Append("            => global::SomeEngine.Views.Scope.").Append(method.Name)
            .Append(GenerateTypeParameters(method.TypeParameters)).Append('(').Append(GenerateArguments(method.Parameters))
            .Append(", ").Append(key.ToString(CultureInfo.InvariantCulture)).AppendLine("UL);");
    }
}
