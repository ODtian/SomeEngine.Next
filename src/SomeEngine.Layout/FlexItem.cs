using SomeEngine.ECS;

namespace SomeEngine.Layout;

/// <summary>Caller-written sizing weights, main-axis basis, and cross-axis alignment for a Flex child.</summary>
public struct FlexItem : IComponent
{
    public float Grow;
    private float? _shrink;
    private float? _basis;
    public Align Self;

    public float Shrink { readonly get => _shrink ?? 1; set => _shrink = value; }
    public float Basis { readonly get => _basis ?? float.NaN; set => _basis = value; }
}
