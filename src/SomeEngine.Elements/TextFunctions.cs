using System.CodeDom.Compiler;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static partial class TextFunctions
{
    [GeneratedCode("SomeEngine.Views.Generators", "1.0")]
    public static Entity Text(in LocalizedText text)
    {
        DevelopmentCheck.Fail("Text calls require source-generator interception; use DeclareText with an explicit key.");
        return DeclareText(0, in text);
    }

    public static Entity DeclareText(ulong callKey, in LocalizedText text) =>
        Scope.View(new Text(in text), callKey);

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
        {
            IFormattedText source = previous.FormattedValue is FormattedText<T> formatted && formatted.Equals(data)
                ? previous.FormattedValue : data;
            return new Text(TextFormatting.Format(data.Value, previous.Value, data.Format,
                data.Provider ?? Locale.ReadCulture(Scope.World, track: false))) { FormattedValue = source };
        }, callKey);
    }

    /// <summary>Writes caller-owned text directly, retaining its write version when the text matches.</summary>
    public static Entity SetText(this Entity node, World world, string value)
    {
        Text previous = world.Read<Text>(node);
        return WriteText(node, world, in previous, new Text(value));
    }

    /// <summary>Formats into reusable temporary characters and creates a string only when the text changes.</summary>
    public static Entity SetText<T>(this Entity node, T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        ref Text text = ref Scope.Write<Text>(node);
        var data = new FormattedText<T>(value, format, provider);
        text = CreateWrittenText(in text, in data, provider ?? Locale.ReadCulture(Scope.World, track: true));
        return node;
    }

    /// <summary>Formats into reusable temporary characters and creates a string only when the text changes.</summary>
    public static Entity SetText<T>(this Entity node, World world, T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable
    {
        Text previous = world.Read<Text>(node);
        var data = new FormattedText<T>(value, format, provider);
        Text text = CreateWrittenText(in previous, in data, provider ?? Locale.ReadCulture(world, track: false));
        return WriteText(node, world, in previous, in text);
    }

    private static Entity WriteText(Entity node, World world, in Text previous, in Text text)
    {
        if (!previous.Equals(text))
            world.Replace(node, in text);
        return node;
    }

    private static Text CreateWrittenText<T>(in Text previous, in FormattedText<T> data, IFormatProvider? provider)
        where T : struct, ISpanFormattable
    {
        string value = TextFormatting.Format(data.Value, previous.Value, data.Format, provider);
        if (ReferenceEquals(value, previous.Value) && previous.LocalizedValue is null &&
            previous.FormattedValue is FormattedText<T> formatted &&
            formatted.Equals(data))
            return previous;
        return new Text(value);
    }

    private readonly record struct FormattedText<T>(T Value, string Format, IFormatProvider? Provider) : IFormattedText
        where T : struct, ISpanFormattable
    {
        public bool Equals(FormattedText<T> other) =>
            Format == other.Format && ReferenceEquals(Provider, other.Provider) && TextFormatting.ValuesEqual(Value, other.Value);

        public override int GetHashCode() => HashCode.Combine(Value, Format, Provider);

        string IFormattedText.Format(string? previous, IFormatProvider? provider) =>
            TextFormatting.Format(Value, previous, Format, provider);
    }
}
