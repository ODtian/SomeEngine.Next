using System.CodeDom.Compiler;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static partial class TextFunctions
{
    [GeneratedCode("SomeEngine.Views.Generators", "1.0")]
    public static Entity Text(int value, string format = "")
    {
        DevelopmentCheck.Fail("Text calls require source-generator interception; use DeclareText with an explicit key.");
        return DeclareText(0, value, format);
    }

    [GeneratedCode("SomeEngine.Views.Generators", "1.0")]
    public static Entity Text<T>(T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        DevelopmentCheck.Fail("Text calls require source-generator interception; use DeclareText with an explicit key.");
        return DeclareText(0, value, format, provider);
    }

    public static Entity DeclareText(ulong callKey, int value, string format = "") =>
        DeclareText<int>(callKey, value, format);

    public static Entity DeclareText<T>(ulong callKey, T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        var data = new FormattedText<T>(value, format, provider);
        return Scope.View<Text, FormattedText<T>>(in data, static (in FormattedText<T> data, in Text previous) =>
            new Text(TextFormatting.Format(data.Value, previous.Value, data.Format, data.Provider)), callKey);
    }

    /// <summary>Writes caller-owned text directly, retaining its write version when the text matches.</summary>
    public static Entity SetText(this Entity node, World world, string value)
    {
        if (world.Read<Text>(node).Value != value)
            world.Replace(node, new Text(value));
        return node;
    }

    /// <summary>Formats into reusable temporary characters and creates a string only when the text changes.</summary>
    public static Entity SetText<T>(this Entity node, T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        ref Text text = ref Scope.Write<Text>(node);
        text.Value = TextFormatting.Format(value, text.Value, format, provider);
        return node;
    }

    /// <summary>Formats into reusable temporary characters and creates a string only when the text changes.</summary>
    public static Entity SetText<T>(this Entity node, World world, T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        string previous = world.Read<Text>(node).Value;
        string text = TextFormatting.Format(value, previous, format, provider);
        if (!ReferenceEquals(text, previous))
            world.Replace(node, new Text(text));
        return node;
    }

    private readonly record struct FormattedText<T>(T Value, string Format, IFormatProvider? Provider);
}
