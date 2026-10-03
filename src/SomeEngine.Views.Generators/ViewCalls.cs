using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

internal static class ViewCalls
{
    internal static bool IsComposable(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute");

    internal static bool IsComposeView(IMethodSymbol method) => IsComposable(method) && method.IsStatic &&
        (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "SomeEngine.Views.BuildTask");

    internal static bool IsComposeBuild(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators.Compose");

    internal static bool IsView(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Scope" &&
        method.Name == "View" && method.Parameters.Length == 1;

    internal static bool IsRemember(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Compose.Compose" &&
        method.Name == "Remember" && method.Parameters.Length == 1;

    internal static bool IsViewFunction(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators");
}
