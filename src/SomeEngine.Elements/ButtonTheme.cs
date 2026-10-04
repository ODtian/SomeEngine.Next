using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The nearest button colors and spacing; omitted properties use the default button style.</summary>
public struct ButtonTheme : IComponent
{
    private Color? _fill;
    private Color? _hoverFill;
    private Color? _pressedFill;
    private Color? _textColor;
    private Thickness? _padding;
    private float? _cornerRadius;

    public Color Fill { readonly get => _fill ?? new Color(0.2f, 0.2f, 0.2f); set => _fill = value; }
    public Color HoverFill { readonly get => _hoverFill ?? new Color(0.3f, 0.3f, 0.3f); set => _hoverFill = value; }
    public Color PressedFill { readonly get => _pressedFill ?? new Color(0.1f, 0.1f, 0.1f); set => _pressedFill = value; }
    public Color TextColor { readonly get => _textColor ?? new Color(1, 1, 1); set => _textColor = value; }
    public Thickness Padding { readonly get => _padding ?? new Thickness(8, 8, 8, 8); set => _padding = value; }
    public float CornerRadius { readonly get => _cornerRadius ?? 4; set => _cornerRadius = value; }
}
