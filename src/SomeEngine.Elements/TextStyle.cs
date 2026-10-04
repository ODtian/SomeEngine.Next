using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.Typography;

namespace SomeEngine.Elements;

/// <summary>Caller-written text style fields; omitted fields use the nearest TextTheme.</summary>
public record struct TextStyle : IComponent
{
    public Font? Font;
    private float? _size;
    private Color? _color;
    private float? _lineHeight;
    public float Size { readonly get => _size ?? 16; set => _size = value; }
    public Color Color { readonly get => _color ?? new Color(1, 1, 1); set => _color = value; }
    /// <summary>Pixels between baselines; zero uses the font's line metrics.</summary>
    public float LineHeight { readonly get => _lineHeight ?? 0; set => _lineHeight = value; }

    internal readonly bool NeedsDefaults => Font is null || _size is null || _color is null || _lineHeight is null;

    internal readonly TextStyle WithDefaults(in TextStyle defaults) => new()
    {
        Font = Font ?? defaults.Font,
        Size = _size ?? defaults.Size,
        Color = _color ?? defaults.Color,
        LineHeight = _lineHeight ?? defaults.LineHeight,
    };
}

/// <summary>The nearest complete default text style supplied by an ElementTree ancestor.</summary>
public readonly record struct TextTheme(TextStyle Style) : IComponent;
