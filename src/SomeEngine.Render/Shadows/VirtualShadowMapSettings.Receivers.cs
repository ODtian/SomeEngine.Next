namespace SomeEngine.Render.Shadows;

public sealed partial record VirtualShadowMapSettings
{
    public uint MaximumViewsPerLight => Math.Max(6u, DirectionalClipmapLevels);
    public bool CullBackfacingPixels { get; init; } = true;
    public float ScreenRayLength { get; init; } = .015f;
    public float DirectionalPageDilationBorder { get; init; } = .05f;
    public float LocalPageDilationBorder { get; init; } = .05f;
}
