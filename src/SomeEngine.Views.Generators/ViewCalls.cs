using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

internal static class ViewCalls
{
    internal static bool IsView(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Scope" &&
        method.Name == "View" && method.Parameters.Length == 1;

    internal static bool IsViewFunction(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators");
}
