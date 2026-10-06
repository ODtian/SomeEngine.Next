namespace SomeEngine.Render.Shadows;

public sealed partial record VirtualShadowMapSettings
{
    public bool CullBackfacingPixels { get; init; } = true;
    public float FirstPersonPixelRequestBias { get; init; } = 2f;
    public int FirstPersonPixelRequestMinLevel { get; init; } = 8;
    public float DirectionalPageDilationBorder { get; init; } = .05f;
    public float LocalPageDilationBorder { get; init; } = .05f;
}
