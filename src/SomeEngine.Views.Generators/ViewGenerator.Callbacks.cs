using System.Text;
using Microsoft.CodeAnalysis;

namespace SomeEngine.Views.Generators;

public sealed partial class ViewGenerator
{
    private static readonly SymbolDisplayFormat s_callbackTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string GenerateCallbackArgument(string name, ITypeSymbol type)
    {
        string callback = "(" + type.ToDisplayString(s_callbackTypeFormat) + ")callbacks.@" + name;
        return type.NullableAnnotation == NullableAnnotation.Annotated
            ? "(this.@" + name + " is null ? null : " + callback + ")" : callback;
    }

    private static void GenerateCallbackFunctions(StringBuilder text, string viewName, IMethodSymbol method,
        IEnumerable<(string Name, ITypeSymbol Type)> callbacks)
    {
        string typeParameters = GenerateTypeParameters(method.TypeParameters);
        text.Append("    private sealed class @").Append(viewName).Append("Callbacks").Append(typeParameters)
            .AppendLine("(global::SomeEngine.ECS.World world, global::SomeEngine.ECS.Entities.Entity entity)")
            .AppendLine(GenerateConstraints(method.TypeParameters)).AppendLine("    {");
        foreach (var callback in callbacks)
        {
            IMethodSymbol invoke = ((INamedTypeSymbol)callback.Type).DelegateInvokeMethod!;
            text.Append("        public ").Append(invoke.ReturnType.ToDisplayString(s_callbackTypeFormat))
                .Append(" @").Append(callback.Name).Append('(')
                .Append(string.Join(", ", invoke.Parameters.Select((parameter, index) =>
                    parameter.Type.ToDisplayString(s_callbackTypeFormat) + " arg" + index)))
                .Append(") => world.ReadSparse<@").Append(viewName).Append(typeParameters).Append(">(entity).@")
                .Append(callback.Name).Append("!(")
                .Append(string.Join(", ", invoke.Parameters.Select((_, index) => "arg" + index))).AppendLine(");");
        }
        text.AppendLine("    }");
    }
}
