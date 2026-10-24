using SomeEngine.ECS;
using SomeEngine.Typography;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements;

/// <summary>Read-only text layout written by the text shaping system.</summary>
public readonly struct ShapedText : IComponent
{
    private readonly TextShapingSystem.TextMeasurement? _measurement;
    public TextShape Shape => _measurement?.Current.Buffer.Shape ?? default;
    public TextStyle Style { get; }
    public Size Size => new(Shape.Size.Width, Shape.Size.Height);
    public uint Version { get; }

    internal ShapedText(TextShapingSystem.TextMeasurement measurement, in TextStyle style, uint version)
    {
        _measurement = measurement;
        Style = style;
        Version = version;
    }
}
