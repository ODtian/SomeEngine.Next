using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The nearest dialog appearance and spacing; omitted properties use the default dialog style.</summary>
public struct DialogTheme : IComponent
{
    private Color? _fill;
    private float? _cornerRadius;
    private DropShadow? _shadow;
    private float? _gap;
    private Color? _titleColor;
    private float? _titleSize;
    private float? _actionsGap;

    public Color Fill { readonly get => _fill ?? new Color(0.15f, 0.15f, 0.15f); set => _fill = value; }
    public float CornerRadius { readonly get => _cornerRadius ?? 8; set => _cornerRadius = value; }
    public DropShadow Shadow
    {
        readonly get => _shadow ?? new DropShadow(new Color(0, 0, 0, 0.35f), new Vector2(0, 4), 12);
        set => _shadow = value;
    }
    public float Gap { readonly get => _gap ?? 16; set => _gap = value; }
    public Color TitleColor { readonly get => _titleColor ?? new Color(1, 1, 1); set => _titleColor = value; }
    public float TitleSize { readonly get => _titleSize ?? 24; set => _titleSize = value; }
    public float ActionsGap { readonly get => _actionsGap ?? 8; set => _actionsGap = value; }
}
