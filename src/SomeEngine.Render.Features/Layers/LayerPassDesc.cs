namespace SomeEngine.Render.Features.Layers;

public enum LayerSortMode : uint
{
    DepthFrontToBack,
    DrawOrderThenDepth,
    DepthBackToFront,
}

public enum LayerAccumulator : uint
{
    Color,
    ColorDepthStencil,
}

public readonly record struct LayerPassDesc(string Name,
    LayerSortMode SortMode = LayerSortMode.DepthFrontToBack,
    bool ClipByOpaqueDepth = true,
    LayerAccumulator Accumulator = LayerAccumulator.Color);
