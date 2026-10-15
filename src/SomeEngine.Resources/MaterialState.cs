namespace SomeEngine.Resources;

public enum CompareOperation
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

public enum SurfaceMode
{
    Opaque,
    Masked,
    Translucent,
}

public enum StencilOp
{
    Keep,
    Zero,
    Replace,
    IncrementSaturate,
    DecrementSaturate,
    Invert,
    IncrementWrap,
    DecrementWrap,
}

public readonly record struct MaterialState
{
    public SurfaceMode Surface { get; init; }
    public bool TwoSided { get; init; }
    public int OverlayLayer { get; init; }
    public float BoundsExpansion { get; init; }
    /// <summary>Upper bound on each shaded color component when limiting the transparent tail.</summary>
    public float ColorLimit { get; init; }
    /// <summary>Maximum color error from the remaining transparent tail. Zero evaluates every contribution.</summary>
    public float MaximumError { get; init; }
    public byte StencilRef { get; init; }
    public CompareOperation StencilCompare { get; init; }
    public StencilOp StencilPass { get; init; }

    public static MaterialState Default => new()
    {
        Surface = SurfaceMode.Opaque,
        ColorLimit = 1,
        StencilCompare = CompareOperation.Always,
        StencilPass = StencilOp.Keep,
    };
}
