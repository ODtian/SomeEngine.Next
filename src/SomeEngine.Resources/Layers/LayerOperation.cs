using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources.Layers;

public enum LayerColorOperation : uint
{
    Over,
    Transmit,
    Add,
    Multiply,
    Min,
    Max,
}

public enum LayerDepthTarget : uint
{
    None,
    Accumulator,
    Opaque,
}

/// <summary>Data interpreted by the shared color, depth, stencil and X-ray composition kernel.</summary>
public readonly record struct LayerOperation
{
    public LayerColorOperation Color { get; init; }
    public ColorWriteMasks ColorWriteMask { get; init; }
    public bool Opaque { get; init; }
    public LayerDepthTarget DepthTestTarget { get; init; }
    public LayerDepthTarget DepthWriteTarget { get; init; }
    public CompareOperation DepthComparison { get; init; }
    public bool StencilTest { get; init; }
    public byte StencilReadMask { get; init; }
    public byte StencilWriteMask { get; init; }
    public StencilFaceState Front { get; init; }
    public StencilFaceState Back { get; init; }
    public bool XRay { get; init; }
    public Vector4 VisibleColor { get; init; }
    public Vector4 OccludedColor { get; init; }

    public LayerOperation()
    {
        ColorWriteMask = ColorWriteMasks.All;
        DepthComparison = CompareOperation.LessOrEqual;
        StencilReadMask = byte.MaxValue;
        StencilWriteMask = byte.MaxValue;
        Front = new();
        Back = new();
        VisibleColor = Vector4.One;
        OccludedColor = Vector4.One;
    }
}
