using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;

namespace SomeEngine.Render.Components;

/// <summary>Scales the surface's own color and opacity before compositing.</summary>
public readonly record struct XrayBlend(Vector3 ColorMultiplier, float Opacity)
{
    public XrayBlend(float opacity) : this(Vector3.One, opacity) { }
}

/// <summary>Visible and occluded surface rules. Occlusion distances use view-space world units.</summary>
public readonly record struct Xray(XrayBlend Visible, XrayBlend Occluded,
    float MinimumOcclusion = 0, float MaximumOcclusion = float.MaxValue) : IComponent;

/// <summary>Extracted X-ray rules stored with the mesh's other GPU instance properties.</summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public readonly record struct RenderXray : IComponent
{
    public readonly Vector4 Visible;
    public readonly Vector4 Occluded;
    public readonly float MinimumOcclusion;
    public readonly float MaximumOcclusion;
    public readonly uint Enabled;

    public RenderXray(Xray value)
    {
        Visible = new(value.Visible.ColorMultiplier, value.Visible.Opacity);
        Occluded = new(value.Occluded.ColorMultiplier, value.Occluded.Opacity);
        MinimumOcclusion = value.MinimumOcclusion;
        MaximumOcclusion = value.MaximumOcclusion;
        Enabled = 1;
    }
}
