using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Caller-written text whose measured content comes from the text shaping system.</summary>
public record struct Text(string Value) : IView, IComponent, ILayout
{
    public LocalizedText? LocalizedValue { get; init; }
    internal IFormattedText? FormattedValue { get; init; }

    public Text(in LocalizedText text) : this("") => LocalizedValue = text;

    public BuildTask Build()
    {
        ref TextContent content = ref Scope.Write<TextContent>(Scope.Entity);
        string value = Value;
        if (LocalizedValue is { } localized)
        {
            Entity source = Locale.Find(Scope.World);
            string language = Scope.Read<Locale>(source).Name;
            DevelopmentCheck.Require(Scope.World.Has<Translations>(source),
                "Localized text requires Translations beside Locale.");
            Translations translations = Scope.Read<Translations>(source);
            value = translations.Resolve(language, in localized);
        }
        else if (FormattedValue is { } formatted)
        {
            IFormatProvider? provider = formatted.Provider ?? Locale.ReadCulture(Scope.World, track: true);
            value = formatted.Format(content.Value, provider);
        }
        content = new TextContent(value);
        TextStyle style = Scope.Has<TextStyle>(Scope.Entity) ? Scope.Read<TextStyle>(Scope.Entity) : default;
        TextStyle defaults = style.NeedsDefaults ? Scope.ReadUp<TextTheme>().Style : default;
        Scope.Write<TextAppearance>(Scope.Entity) = new TextAppearance(style.WithDefaults(in defaults));
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        DevelopmentCheck.Require(pass.Has<ShapedText>(pass.Node),
            "Text measurement requires TextShapingSystem registered with LayoutSystem.");
        return constraints.Constrain(pass.Read<ShapedText>(pass.Node).Size);
    }
}

/// <summary>The displayed string resolved by a text view's build.</summary>
public readonly record struct TextContent(string Value) : IComponent;

internal readonly record struct TextAppearance(TextStyle Style) : IComponent;
