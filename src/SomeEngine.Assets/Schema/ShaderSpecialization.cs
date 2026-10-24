using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Ordered Slang expressions for one offline-compiled shader specialization.</summary>
[BinarySerializable(BinaryCompatibility.ExactSchema), TextSerializable]
public sealed partial record ShaderSpecialization
{
    public string EntryPoint { get; init; } = string.Empty;
    public ImmutableList<string> Arguments { get; init; } = [];

    internal SpecializationArg[] ToSlangArguments() =>
        Arguments.Select(SpecializationArg.FromExpression).ToArray();

    internal static string[] Expressions(ReadOnlySpan<SpecializationArg> arguments)
    {
        var expressions = new string[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            SpecializationArg argument = arguments[index];
            if (argument.Kind != SpecializationArgKind.Expr)
                throw new ArgumentException("Saved shader specializations require expression arguments.", nameof(arguments));
            expressions[index] = argument.Value.Expression
                ?? throw new ArgumentException("A shader specialization expression cannot be null.", nameof(arguments));
        }
        return expressions;
    }
}
