namespace SomeEngine.Resources.Layers;

public readonly record struct LayerPassId(uint Value);

/// <summary>A material's layer pass, author order and composition values.</summary>
public readonly record struct MaterialLayer(
    LayerPassId Pass,
    byte DrawOrder,
    LayerOperation Operation,
    byte StencilReference = 0);
