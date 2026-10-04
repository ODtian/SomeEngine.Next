using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Caller-written text whose measured content comes from the text shaping system.</summary>
public record struct Text(string Value) : IView, IComponent, ILayout
{
    public static BuildMode Mode => BuildMode.Once;

    public LocalizedText? LocalizedValue { get; init; }
    internal IFormattedText? FormattedValue { get; init; }

    public Text(in LocalizedText text) : this("") => LocalizedValue = text;

    public BuildTask Build()
    {
        Scope.Compute(ResolveContent);
        Scope.Compute(ResolveStyle);
        return default;
    }

    private static void ResolveContent()
    {
        Text text = Scope.Read<Text>(Scope.Entity);
        ref TextContent content = ref Scope.Write<TextContent>(Scope.Entity);
        string value = text.Value;
        if (text.LocalizedValue is { } localized)
        {
            Entity source = Locale.Find(Scope.World);
            string language = Scope.Read<Locale>(source).Name;
            DevelopmentCheck.Require(Scope.World.Has<Translations>(source),
                "Localized text requires Translations beside Locale.");
            Translations translations = Scope.Read<Translations>(source);
            value = translations.Resolve(language, in localized);
        }
        else if (text.FormattedValue is { } formatted)
        {
            IFormatProvider? provider = formatted.Provider ?? Locale.ReadCulture(Scope.World, track: true);
            value = formatted.Format(content.Value, provider);
        }
        content = new TextContent(value);
    }

    private static void ResolveStyle()
    {
        TextStyle style = Scope.Has<TextStyle>(Scope.Entity) ? Scope.Read<TextStyle>(Scope.Entity) : default;
        TextStyle defaults = style.NeedsDefaults ? Scope.ReadUp<TextTheme>().Style : default;
        Scope.Write<TextAppearance>(Scope.Entity) = new TextAppearance(style.WithDefaults(in defaults));
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
