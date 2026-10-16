using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public enum SurfaceMode
{
    Opaque,
    Masked,
    Translucent,
}

public readonly record struct MaterialState
{
    public SurfaceMode Surface { get; init; }
    public bool TwoSided { get; init; }
    public float BoundsExpansion { get; init; }
    /// <summary>Upper bound on each shaded color component when limiting the transparent tail.</summary>
    public float ColorLimit { get; init; }
    /// <summary>Maximum color error from the remaining transparent tail. Zero evaluates every contribution.</summary>
    public float MaximumError { get; init; }
    public byte StencilRef { get; init; }
    /// <summary>Runs this material after depth-sorted transparency, in DrawOrder and primitive order.</summary>
    public bool Ordered { get; init; }
    public int DrawOrder { get; init; }
    public BlendAttachmentState Blend { get; init; }
    public Vector4 BlendConstant { get; init; }
    public DepthStencilState DepthStencil { get; init; }

    public static MaterialState Default => new()
    {
        Surface = SurfaceMode.Opaque,
        ColorLimit = 1,
        Blend = new(),
        DepthStencil = new(DepthTest: true, DepthComparison: CompareOperation.LessOrEqual,
            Front: new(), Back: new()),
    };
}
