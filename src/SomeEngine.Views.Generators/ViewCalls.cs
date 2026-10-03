using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

internal static class ViewCalls
{
    internal static bool IsComposable(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute");

    internal static bool IsComposeView(IMethodSymbol method) => IsComposable(method) && method.IsStatic &&
        (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "SomeEngine.Views.BuildTask");

    internal static bool IsContentParameter(IParameterSymbol parameter) =>
        IsContentDelegate(parameter.Type) && parameter.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute");

    internal static bool IsContentDelegate(ITypeSymbol type) => type is INamedTypeSymbol
        { DelegateInvokeMethod: { } method } && (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "SomeEngine.Views.BuildTask") &&
        method.Parameters.All(parameter => parameter.RefKind == RefKind.None);

    internal static bool IsComposeBuild(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators.Compose");

    internal static bool IsView(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Scope" &&
        method.Name == "View" && method.Parameters.Length == 1;

    internal static bool IsViewFunction(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators");
}
