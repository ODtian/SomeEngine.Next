using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

internal static class ViewCalls
{
    internal static bool IsComposable(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.ComposableAttribute");

    internal static bool IsComponent(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Solid.ComponentAttribute");

    internal static bool IsSolidComponent(IMethodSymbol method) => IsComponent(method) && method.IsStatic &&
        (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "SomeEngine.Views.BuildTask") &&
        method.Parameters.All(parameter => parameter.RefKind == RefKind.None);

    internal static bool IsComponentCall(SemanticModel model, int position)
    {
        ISymbol? caller = model.GetEnclosingSymbol(position);
        while (caller is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambda)
        {
            if (IsComposable(lambda))
                return true;
            caller = caller.ContainingSymbol;
        }
        if (caller is not IMethodSymbol method || method.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators.Solid"))
            return false;
        if (IsComposable(method))
            return true;
        ISymbol? build = model.Compilation.GetTypeByMetadataName("SomeEngine.Views.IView")?.GetMembers("Build").FirstOrDefault();
        return build is not null && SymbolEqualityComparer.Default.Equals(method,
            method.ContainingType.FindImplementationForInterfaceMember(build));
    }

    internal static bool IsComposeView(IMethodSymbol method) => IsComposable(method) && method.IsStatic &&
        !IsComposeGroup(method) && (method.ReturnsVoid || method.ReturnType.ToDisplayString() == "SomeEngine.Views.BuildTask");

    internal static bool IsComposeGroup(IMethodSymbol method) => IsComposable(method) && method.IsStatic &&
        ((!method.ReturnsVoid && method.ReturnType.ToDisplayString() != "SomeEngine.Views.BuildTask") ||
            method.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "SomeEngine.Views.Compose.InlineAttribute"));

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

    internal static bool IsRemember(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Compose.Compose" &&
        method.Name == "Remember" && method.Parameters.Length == 1;

    internal static bool IsAsync(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == "SomeEngine.Views.Scope" &&
        method.Name is "Launch" or "Resource" && method.Parameters.Length == 2;

    internal static bool IsViewFunction(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
        attribute.ConstructorArguments.FirstOrDefault().Value as string == "SomeEngine.Views.Generators");
}
